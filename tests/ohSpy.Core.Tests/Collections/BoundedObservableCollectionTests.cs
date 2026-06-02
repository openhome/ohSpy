namespace ohSpy.Core.Tests.Collections;

using System.Collections.Specialized;
using System.Diagnostics;
using FluentAssertions;
using ohSpy.Core.Collections;

public class BoundedObservableCollectionTests
{
    [Fact]
    public void Constructor_RejectsZeroCapacity()
    {
        Action act = () => new BoundedObservableCollection<int>(0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Constructor_RejectsNegativeCapacity()
    {
        Action act = () => new BoundedObservableCollection<int>(-1);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Constructor_AcceptsPositiveCapacity()
    {
        var coll = new BoundedObservableCollection<int>(10);
        coll.Capacity.Should().Be(10);
        coll.Count.Should().Be(0);
    }

    [Fact]
    [Trait("ac", "AC-6.1")]
    public void PrependNewest_BelowCapacity_EmitsAddAtIndexZero()
    {
        var coll = new BoundedObservableCollection<int>(10);
        var notifications = new List<NotifyCollectionChangedEventArgs>();
        coll.CollectionChanged += (_, e) => notifications.Add(e);

        coll.PrependNewest(1);
        coll.PrependNewest(2);
        coll.PrependNewest(3);

        notifications.Should().HaveCount(3);
        notifications.Should().OnlyContain(n => n.Action == NotifyCollectionChangedAction.Add);
        notifications.Should().OnlyContain(n => n.NewStartingIndex == 0);
        notifications[0].NewItems![0].Should().Be(1);
        notifications[1].NewItems![0].Should().Be(2);
        notifications[2].NewItems![0].Should().Be(3);

        coll.Count.Should().Be(3);
        coll[0].Should().Be(3); // newest
        coll[2].Should().Be(1); // oldest
    }

    [Fact]
    [Trait("ac", "AC-6.1")]
    public void PrependNewest_AtCapacity_EmitsAddThenRemoveNeverReset()
    {
        var coll = new BoundedObservableCollection<int>(3);
        var notifications = new List<NotifyCollectionChangedEventArgs>();
        coll.CollectionChanged += (_, e) => notifications.Add(e);

        coll.PrependNewest(1);
        coll.PrependNewest(2);
        coll.PrependNewest(3);

        // Fill phase: three Add(0) notifications.
        notifications.Should().HaveCount(3);
        notifications.Should().OnlyContain(n => n.Action == NotifyCollectionChangedAction.Add);

        // Overflow: 4th prepend evicts item 1 (the oldest).
        coll.PrependNewest(4);

        // Total now: 3 fills + 2 (Add + Remove) = 5.
        notifications.Should().HaveCount(5);

        // 4th notification: Add(item=4, index=0).
        notifications[3].Action.Should().Be(NotifyCollectionChangedAction.Add);
        notifications[3].NewStartingIndex.Should().Be(0);
        notifications[3].NewItems.Should().NotBeNull();
        notifications[3].NewItems![0].Should().Be(4);

        // 5th notification: Remove(item=1, index=3 == capacity).
        notifications[4].Action.Should().Be(NotifyCollectionChangedAction.Remove);
        notifications[4].OldStartingIndex.Should().Be(3);
        notifications[4].OldItems.Should().NotBeNull();
        notifications[4].OldItems![0].Should().Be(1);

        // NEVER Reset.
        notifications.Should().NotContain(n => n.Action == NotifyCollectionChangedAction.Reset);

        // Final state.
        coll.Count.Should().Be(3);
        coll[0].Should().Be(4); // newest
        coll[1].Should().Be(3);
        coll[2].Should().Be(2); // oldest after evicting 1
    }

    [Fact]
    [Trait("ac", "AC-6.2")]
    public void PrependNewest_HundredKOnTenKRing_IsO1AndNeverEmitsReset()
    {
        const int Capacity = 10_000;
        const int Iterations = 100_000;

        var coll = new BoundedObservableCollection<int>(Capacity);
        int notificationCount = 0;
        int resetCount = 0;
        coll.CollectionChanged += (_, e) =>
        {
            notificationCount++;
            if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                resetCount++;
            }
        };

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < Iterations; i++)
        {
            coll.PrependNewest(i);
        }
        sw.Stop();

        // Generous 1s wall-clock bound — actual is microseconds.
        sw.ElapsedMilliseconds.Should().BeLessThan(1000,
            $"100K PrependNewest on a 10K-capacity ring should be O(N). Took {sw.ElapsedMilliseconds} ms.");
        coll.Count.Should().Be(Capacity);
        resetCount.Should().Be(0);

        // Expected notifications: Capacity Add-only events for the fill phase,
        // then (Iterations - Capacity) * 2 (Add + Remove) for overflow.
        int expected = Capacity + ((Iterations - Capacity) * 2);
        notificationCount.Should().Be(expected);
    }

    [Fact]
    [Trait("ac", "AC-6.6")]
    public void Clear_EmitsSingleResetNotification()
    {
        var coll = new BoundedObservableCollection<int>(5);
        coll.PrependNewest(1);
        coll.PrependNewest(2);
        coll.PrependNewest(3);

        var notifications = new List<NotifyCollectionChangedEventArgs>();
        coll.CollectionChanged += (_, e) => notifications.Add(e);

        coll.Clear();

        notifications.Should().HaveCount(1);
        notifications[0].Action.Should().Be(NotifyCollectionChangedAction.Reset);
        coll.Count.Should().Be(0);

        // Subsequent prepend on cleared collection works normally.
        coll.PrependNewest(99);
        coll.Count.Should().Be(1);
        coll[0].Should().Be(99);
    }

    [Fact]
    public void Indexer_OutOfRange_Throws()
    {
        var coll = new BoundedObservableCollection<int>(5);
        coll.PrependNewest(1);

        Action negative = () => { var _ = coll[-1]; };
        Action atCount = () => { var _ = coll[coll.Count]; };
        negative.Should().Throw<ArgumentOutOfRangeException>();
        atCount.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Enumeration_YieldsNewestFirst()
    {
        var coll = new BoundedObservableCollection<int>(5);
        coll.PrependNewest(10);
        coll.PrependNewest(20);
        coll.PrependNewest(30);

        coll.ToList().Should().Equal(30, 20, 10);
    }

    [Fact]
    public void PrependNewest_RingWrap_PreservesNewestFirstEnumeration()
    {
        // Force the ring buffer to wrap multiple times; assert logical ordering remains correct.
        var coll = new BoundedObservableCollection<int>(3);
        for (int i = 1; i <= 10; i++)
        {
            coll.PrependNewest(i);
        }

        // Newest 3: 10, 9, 8.
        coll.Count.Should().Be(3);
        coll[0].Should().Be(10);
        coll[1].Should().Be(9);
        coll[2].Should().Be(8);
        coll.ToList().Should().Equal(10, 9, 8);
    }
}
