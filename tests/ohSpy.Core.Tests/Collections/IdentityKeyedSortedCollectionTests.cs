namespace ohSpy.Core.Tests.Collections;

using System.Collections.Specialized;
using FluentAssertions;
using ohSpy.Core.Collections;

public class IdentityKeyedSortedCollectionTests
{
    private sealed record Item(Guid Id, string Name);

    private static IdentityKeyedSortedCollection<Guid, Item> CreateCollection() =>
        new(
            identitySelector: x => x.Id,
            sortComparer: Comparer<Item>.Create((a, b) =>
                string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase)));

    [Fact]
    public void Add_PreservesSortOrder_AndEmitsAddAtCorrectIndex()
    {
        var coll = CreateCollection();
        var notifications = new List<NotifyCollectionChangedEventArgs>();
        coll.CollectionChanged += (_, e) => notifications.Add(e);

        var charlie = new Item(Guid.NewGuid(), "Charlie");
        var alpha = new Item(Guid.NewGuid(), "Alpha");
        var bravo = new Item(Guid.NewGuid(), "Bravo");

        coll.Add(charlie); // → [Charlie@0]
        coll.Add(alpha);   // → [Alpha@0, Charlie@1]
        coll.Add(bravo);   // → [Alpha@0, Bravo@1, Charlie@2]

        notifications.Should().HaveCount(3);
        notifications.Should().OnlyContain(n => n.Action == NotifyCollectionChangedAction.Add);
        notifications[0].NewStartingIndex.Should().Be(0); // Charlie inserted into empty.
        notifications[1].NewStartingIndex.Should().Be(0); // Alpha sorts before Charlie.
        notifications[2].NewStartingIndex.Should().Be(1); // Bravo sorts between Alpha and Charlie.

        coll.ToList().Should().Equal(alpha, bravo, charlie);
    }

    [Fact]
    public void Add_DuplicateIdentity_Throws()
    {
        var coll = CreateCollection();
        var id = Guid.NewGuid();
        coll.Add(new Item(id, "X"));

        Action act = () => coll.Add(new Item(id, "Y"));
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Remove_AbsentIdentity_ReturnsFalseAndEmitsNothing()
    {
        var coll = CreateCollection();
        coll.Add(new Item(Guid.NewGuid(), "Alpha"));

        var notifications = new List<NotifyCollectionChangedEventArgs>();
        coll.CollectionChanged += (_, e) => notifications.Add(e);

        bool result = coll.Remove(Guid.NewGuid());

        result.Should().BeFalse();
        notifications.Should().BeEmpty();
    }

    [Fact]
    public void Remove_PresentIdentity_ReturnsTrueAndEmitsRemove()
    {
        var coll = CreateCollection();
        var alpha = new Item(Guid.NewGuid(), "Alpha");
        var bravo = new Item(Guid.NewGuid(), "Bravo");
        coll.Add(alpha);
        coll.Add(bravo);

        var notifications = new List<NotifyCollectionChangedEventArgs>();
        coll.CollectionChanged += (_, e) => notifications.Add(e);

        bool result = coll.Remove(alpha.Id);

        result.Should().BeTrue();
        notifications.Should().HaveCount(1);
        notifications[0].Action.Should().Be(NotifyCollectionChangedAction.Remove);
        notifications[0].OldStartingIndex.Should().Be(0);
        notifications[0].OldItems![0].Should().Be(alpha);
        coll.ToList().Should().Equal(bravo);

        // Index reflowed after remove — bravo is now at 0.
        coll.TryGetItem(bravo.Id, out _).Should().BeTrue();
    }

    [Fact]
    [Trait("ac", "AC-6.3")]
    public void Update_UnchangedSortKey_EmitsNothing()
    {
        var coll = CreateCollection();
        var id = Guid.NewGuid();
        coll.Add(new Item(id, "Bravo"));

        var notifications = new List<NotifyCollectionChangedEventArgs>();
        coll.CollectionChanged += (_, e) => notifications.Add(e);

        // Same sort key, different instance.
        var updated = new Item(id, "Bravo");
        coll.Update(updated);

        notifications.Should().BeEmpty();

        // Registry now references the updated instance.
        coll.TryGetItem(id, out var fetched).Should().BeTrue();
        fetched.Should().BeSameAs(updated);
    }

    [Fact]
    [Trait("ac", "AC-6.4")]
    public void Update_ChangedSortKey_EmitsExactlyOneMove()
    {
        var coll = CreateCollection();
        var alpha = new Item(Guid.NewGuid(), "Alpha");
        var bravo = new Item(Guid.NewGuid(), "Bravo");
        var charlie = new Item(Guid.NewGuid(), "Charlie");
        coll.Add(alpha);
        coll.Add(bravo);
        coll.Add(charlie);

        var notifications = new List<NotifyCollectionChangedEventArgs>();
        coll.CollectionChanged += (_, e) => notifications.Add(e);

        // Bravo → Zulu: moves from index 1 → 2.
        var zulu = new Item(bravo.Id, "Zulu");
        coll.Update(zulu);

        notifications.Should().HaveCount(1);
        var n = notifications[0];
        n.Action.Should().Be(NotifyCollectionChangedAction.Move);
        n.OldStartingIndex.Should().Be(1);
        n.NewStartingIndex.Should().Be(2);

        // BCL Move convention: NewItems == OldItems == [updatedItem].
        n.NewItems.Should().NotBeNull();
        n.NewItems!.Count.Should().Be(1);
        n.NewItems[0].Should().Be(zulu);
        n.OldItems.Should().NotBeNull();
        n.OldItems!.Count.Should().Be(1);
        n.OldItems[0].Should().Be(zulu);

        // Final order.
        coll.ToList().Should().Equal(alpha, charlie, zulu);
    }

    [Fact]
    [Trait("ac", "AC-6.5")]
    public void Update_ChangedSortKey_PreservesIdentityAcrossMove()
    {
        var coll = CreateCollection();
        var alpha = new Item(Guid.NewGuid(), "Alpha");
        var bravo = new Item(Guid.NewGuid(), "Bravo");
        var charlie = new Item(Guid.NewGuid(), "Charlie");
        coll.Add(alpha);
        coll.Add(bravo);
        coll.Add(charlie);

        var zulu = new Item(bravo.Id, "Zulu");
        coll.Update(zulu);

        // Identity is preserved: looking up the original id returns the updated item.
        coll.TryGetItem(bravo.Id, out var fetched).Should().BeTrue();
        fetched.Should().Be(zulu);
        fetched.Name.Should().Be("Zulu");

        // No item appears twice — no orphan entries in _indexById.
        var iteratedIds = coll.Select(x => x.Id).ToList();
        iteratedIds.Should().OnlyHaveUniqueItems();
        iteratedIds.Should().Contain(alpha.Id);
        iteratedIds.Should().Contain(bravo.Id);
        iteratedIds.Should().Contain(charlie.Id);
        iteratedIds.Should().HaveCount(3);
    }

    [Fact]
    public void Update_AbsentIdentity_ThrowsKeyNotFoundException()
    {
        var coll = CreateCollection();
        Action act = () => coll.Update(new Item(Guid.NewGuid(), "X"));
        act.Should().Throw<KeyNotFoundException>();
    }

    [Fact]
    [Trait("ac", "AC-6.6")]
    public void Clear_EmitsSingleResetNotification()
    {
        var coll = CreateCollection();
        coll.Add(new Item(Guid.NewGuid(), "A"));
        coll.Add(new Item(Guid.NewGuid(), "B"));
        coll.Add(new Item(Guid.NewGuid(), "C"));

        var notifications = new List<NotifyCollectionChangedEventArgs>();
        coll.CollectionChanged += (_, e) => notifications.Add(e);

        coll.Clear();

        notifications.Should().HaveCount(1);
        notifications[0].Action.Should().Be(NotifyCollectionChangedAction.Reset);
        coll.Count.Should().Be(0);
    }

    [Fact]
    public void Indexer_OutOfRange_Throws()
    {
        var coll = CreateCollection();
        coll.Add(new Item(Guid.NewGuid(), "A"));

        Action negative = () => { var _ = coll[-1]; };
        Action atCount = () => { var _ = coll[coll.Count]; };
        negative.Should().Throw<ArgumentOutOfRangeException>();
        atCount.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Update_MoveToHead_AndMoveToTail_BothWorkCorrectly()
    {
        // Boundary coverage: moves to index 0 and to index Count-1.
        var coll = CreateCollection();
        var a = new Item(Guid.NewGuid(), "Beta");
        var b = new Item(Guid.NewGuid(), "Charlie");
        var c = new Item(Guid.NewGuid(), "Delta");
        coll.Add(a);
        coll.Add(b);
        coll.Add(c);

        // c → "Alpha": moves to head.
        var notifications = new List<NotifyCollectionChangedEventArgs>();
        coll.CollectionChanged += (_, e) => notifications.Add(e);
        var cMoved = new Item(c.Id, "Alpha");
        coll.Update(cMoved);

        notifications.Should().HaveCount(1);
        notifications[0].OldStartingIndex.Should().Be(2);
        notifications[0].NewStartingIndex.Should().Be(0);
        coll.ToList().Should().Equal(cMoved, a, b);

        // Now move a → "Zulu": from index 1 → index 2 (tail).
        notifications.Clear();
        var aMoved = new Item(a.Id, "Zulu");
        coll.Update(aMoved);

        notifications.Should().HaveCount(1);
        notifications[0].OldStartingIndex.Should().Be(1);
        notifications[0].NewStartingIndex.Should().Be(2);
        coll.ToList().Should().Equal(cMoved, b, aMoved);
    }
}
