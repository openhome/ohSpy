namespace ohSpy.Core.Tests.ViewModels;

using System.Collections.Specialized;
using FluentAssertions;
using ohSpy.Core.Discovery;
using ohSpy.Core.Models;
using ohSpy.Core.Tests.Fakes;
using ohSpy.Core.ViewModels;

/// <summary>
/// Story 2.7 — <see cref="SsdpLogViewModel"/> unit tests. Covers AC-2.7.2, AC-2.7.3, AC-2.7.7
/// (and the testable shape of AC-2.7.5 — IsAtTop default).
/// Uses <see cref="InlineUiDispatcher"/> for synchronous Post so assertions are deterministic.
/// </summary>
public sealed class SsdpLogViewModelTests : IDisposable
{
    private readonly InlineUiDispatcher _ui = new();
    private readonly StubDiscoveryService _discovery = new();
    private readonly SsdpLogViewModel _vm;

    public SsdpLogViewModelTests()
    {
        _vm = new SsdpLogViewModel(_discovery, _ui);
    }

    public void Dispose() => _vm.Dispose();

    // Only NTS + Uuid matter for the log; everything else is null.
    private static SsdpAnnouncement Announce(string? nts, Guid? uuid) =>
        new(NT: null, NTS: nts, ST: null, USN: null, Uuid: uuid,
            Location: null, CacheControlMaxAge: null, Server: null, BootId: null, ConfigId: null);

    [Fact]
    [Trait("ac", "AC-2.7.2")]
    public void Alive_PrependsEntry_AC272()
    {
        var g = Guid.NewGuid();

        _discovery.Raise(Announce("ssdp:alive", g));

        _vm.Entries.Count.Should().Be(1);
        _vm.Entries[0].Kind.Should().Be(SsdpLogKind.Alive);
        _vm.Entries[0].Uuid.Should().Be(g);
    }

    [Fact]
    [Trait("ac", "AC-2.7.2")]
    public void Byebye_PrependsEntry_AC272()
    {
        var g = Guid.NewGuid();

        _discovery.Raise(Announce("ssdp:byebye", g));

        _vm.Entries.Count.Should().Be(1);
        _vm.Entries[0].Kind.Should().Be(SsdpLogKind.Byebye);
    }

    [Fact]
    [Trait("ac", "AC-2.7.2")]
    public void Newest_IsAtIndexZero_AC272()
    {
        var g1 = Guid.NewGuid();
        var g2 = Guid.NewGuid();

        _discovery.Raise(Announce("ssdp:alive", g1));
        _discovery.Raise(Announce("ssdp:alive", g2));

        _vm.Entries[0].Uuid.Should().Be(g2); // newest first
        _vm.Entries[1].Uuid.Should().Be(g1);
    }

    [Fact]
    [Trait("ac", "AC-2.7.2")]
    public void NtsCaseInsensitive_AC272()
    {
        _discovery.Raise(Announce("SSDP:ALIVE", Guid.NewGuid()));

        _vm.Entries.Count.Should().Be(1);
        _vm.Entries[0].Kind.Should().Be(SsdpLogKind.Alive);
    }

    [Fact]
    [Trait("ac", "AC-2.7.2")]
    public void OtherNts_Ignored_AC272()
    {
        // Absent NTS (M-SEARCH response) + non-alive/byebye verbs are NOT logged (FR-014/015).
        _discovery.Raise(Announce(null, Guid.NewGuid()));
        _discovery.Raise(Announce("ssdp:update", Guid.NewGuid()));
        _discovery.Raise(Announce("ssdp:discover", Guid.NewGuid()));

        _vm.Entries.Count.Should().Be(0);
    }

    [Fact]
    [Trait("ac", "AC-2.7.2")]
    public void NullUuid_FallsBackToEmpty_AC272()
    {
        _discovery.Raise(Announce("ssdp:alive", uuid: null));

        _vm.Entries.Count.Should().Be(1);
        _vm.Entries[0].Uuid.Should().Be(Guid.Empty);
    }

    [Fact]
    [Trait("ac", "AC-2.7.2")]
    public void Capacity_Is10000_AC272()
    {
        _vm.Entries.Capacity.Should().Be(10_000);
    }

    [Fact]
    [Trait("ac", "AC-2.7.3")]
    public void Eviction_AtCapacity_DropsTail_NoReset_AC273()
    {
        // Record every notification to prove no Reset is emitted across the eviction burst.
        var actions = new List<NotifyCollectionChangedAction>();
        _vm.Entries.CollectionChanged += (_, e) => actions.Add(e.Action);

        // Fill exactly to capacity, tracking the oldest UUID, then push one more.
        var oldest = Guid.NewGuid();
        _discovery.Raise(Announce("ssdp:alive", oldest));
        for (var i = 1; i < 10_000; i++)
        {
            _discovery.Raise(Announce("ssdp:alive", Guid.NewGuid()));
        }
        _vm.Entries.Count.Should().Be(10_000);

        var newest = Guid.NewGuid();
        _discovery.Raise(Announce("ssdp:alive", newest)); // 10,001st — evicts the tail

        _vm.Entries.Count.Should().Be(10_000); // capped (FR-016)
        _vm.Entries[0].Uuid.Should().Be(newest); // newest at top
        _vm.Entries.Should().NotContain(e => e.Uuid == oldest); // oldest discarded
        actions.Should().NotContain(NotifyCollectionChangedAction.Reset); // AC-6.1 invariant
    }

    [Fact]
    [Trait("ac", "AC-2.7.7")]
    public void Clear_EmptiesEntries_EmitsSingleReset_AC277()
    {
        _discovery.Raise(Announce("ssdp:alive", Guid.NewGuid()));
        _discovery.Raise(Announce("ssdp:byebye", Guid.NewGuid()));

        var resetCount = 0;
        _vm.Entries.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Reset) resetCount++;
        };

        _vm.Clear();

        _vm.Entries.Count.Should().Be(0);
        resetCount.Should().Be(1); // single Reset (AC-6.6)
    }

    [Fact]
    [Trait("ac", "AC-2.7.2")]
    public void Dispose_Unsubscribes_NoPrependAfterDispose_AC272()
    {
        _discovery.Raise(Announce("ssdp:alive", Guid.NewGuid())); // subscription works
        _vm.Entries.Count.Should().Be(1);

        _vm.Dispose();
        _discovery.Raise(Announce("ssdp:alive", Guid.NewGuid())); // detached — ignored

        _vm.Entries.Count.Should().Be(1); // unchanged
    }

    [Fact]
    [Trait("ac", "AC-2.7.5")]
    public void IsAtTop_DefaultsTrue_AC275()
    {
        // Fresh VM (empty list) starts at the top so the first arrivals auto-follow.
        _vm.IsAtTop.Should().BeTrue();
    }
}
