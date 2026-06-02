namespace ohSpy.Core.Tests.Diagnostics;

using System.Collections.Generic;
using System.Collections.Specialized;
using FluentAssertions;
using ohSpy.Core.Diagnostics;
using ohSpy.Core.Tests.Fakes;

/// <summary>
/// AC-4 + AC-8.2 / AC-8.3 / AC-8.4 — bounded prepend semantics; snapshot-at-arrival
/// identity / endpoint resolution per FR-041.
/// </summary>
public class DiagnosticRingSinkTests
{
    private static DiagnosticEntry MakeEntry(DiagnosticContext ctx = default, DiagSeverity sev = DiagSeverity.Information) =>
        new(DateTime.UtcNow, sev, "test.category", "test message", ctx);

    [Fact]
    [Trait("ac", "AC-4")]
    public void Push_PrependsToBoundedObservableCollection()
    {
        var sink = new DiagnosticRingSink(new InlineUiDispatcher(), new StaticIdentityLookup(null));

        var e1 = MakeEntry();
        var e2 = MakeEntry();
        var e3 = MakeEntry();
        sink.Push(e1);
        sink.Push(e2);
        sink.Push(e3);

        sink.Entries.Count.Should().Be(3);
        sink.Entries[0].Entry.Should().BeSameAs(e3, "newest entry should be at index 0");
        sink.Entries[1].Entry.Should().BeSameAs(e2);
        sink.Entries[2].Entry.Should().BeSameAs(e1);
    }

    [Fact]
    [Trait("ac", "AC-4")]
    public void Push_AtCapacity_EvictsOldestWithoutReset()
    {
        // Capacity is hard-coded to 5000 in DiagnosticRingSink (FR-041 cap; can't be
        // injected). Push 5001 entries and verify NO Reset notification fires — only
        // Add(0) + Remove(5000) per overflowing push.
        var sink = new DiagnosticRingSink(new InlineUiDispatcher(), new StaticIdentityLookup(null));

        var resetCount = 0;
        sink.Entries.CollectionChanged += (_, args) =>
        {
            if (args.Action == NotifyCollectionChangedAction.Reset)
            {
                resetCount++;
            }
        };

        for (int i = 0; i < 5001; i++)
        {
            sink.Push(MakeEntry());
        }

        sink.Entries.Count.Should().Be(5000);
        resetCount.Should().Be(0, "BoundedObservableCollection MUST emit Add+Remove pairs, never Reset, at capacity");
    }

    [Fact]
    [Trait("ac", "AC-8.3")]
    public void IdentityLabel_NullDeviceUuid_ResolvesToEmDash()
    {
        var sink = new DiagnosticRingSink(new InlineUiDispatcher(), new StaticIdentityLookup("Linn DS"));
        sink.Push(MakeEntry(new DiagnosticContext { Url = "http://x/" }));
        sink.Entries[0].IdentityLabel.Should().Be("—");
    }

    [Fact]
    [Trait("ac", "AC-8.3")]
    public void IdentityLabel_RegistryHitWithFriendlyName_ResolvesToFriendlyName()
    {
        var uuid = Guid.NewGuid();
        var sink = new DiagnosticRingSink(new InlineUiDispatcher(), new StaticIdentityLookup("My Linn DS"));
        sink.Push(MakeEntry(new DiagnosticContext { DeviceUuid = uuid }));
        sink.Entries[0].IdentityLabel.Should().Be("My Linn DS");
    }

    [Fact]
    [Trait("ac", "AC-8.3")]
    public void IdentityLabel_RegistryMiss_ResolvesToUuidColonForm()
    {
        var uuid = Guid.NewGuid();
        var sink = new DiagnosticRingSink(new InlineUiDispatcher(), new StaticIdentityLookup(null));
        sink.Push(MakeEntry(new DiagnosticContext { DeviceUuid = uuid }));
        sink.Entries[0].IdentityLabel.Should().Be($"uuid:{uuid}");
    }

    [Fact]
    [Trait("ac", "AC-4")]
    [Trait("fr", "FR-041")]
    public void IdentityLabel_SnapshotSemantics_DoesNotUpdateOnLaterRegistryChange()
    {
        var uuid = Guid.NewGuid();
        var lookup = new MutableIdentityLookup("X");
        var sink = new DiagnosticRingSink(new InlineUiDispatcher(), lookup);

        sink.Push(MakeEntry(new DiagnosticContext { DeviceUuid = uuid }));
        sink.Entries[0].IdentityLabel.Should().Be("X");

        // Registry name changes between pushes.
        lookup.Name = "Y";
        sink.Push(MakeEntry(new DiagnosticContext { DeviceUuid = uuid }));

        // FR-041 snapshot invariant: the FIRST row's label MUST still be "X" — later
        // registry mutations do not update existing rows. The NEW row (at index 0 now,
        // because PrependNewest) is "Y".
        sink.Entries[0].IdentityLabel.Should().Be("Y");
        sink.Entries[1].IdentityLabel.Should().Be("X",
            "FR-041 snapshot semantics: existing rows are immutable");
    }

    [Fact]
    [Trait("ac", "AC-8.4")]
    public void EndpointLabel_UrlWithDefaultPort_ResolvesToHostOnly()
    {
        var sink = new DiagnosticRingSink(new InlineUiDispatcher(), new StaticIdentityLookup(null));
        sink.Push(MakeEntry(new DiagnosticContext { Url = "http://192.168.1.1/" }));
        sink.Entries[0].EndpointLabel.Should().Be("192.168.1.1");
    }

    [Fact]
    [Trait("ac", "AC-8.4")]
    public void EndpointLabel_UrlWithNonDefaultPort_ResolvesToHostColonPort()
    {
        var sink = new DiagnosticRingSink(new InlineUiDispatcher(), new StaticIdentityLookup(null));
        sink.Push(MakeEntry(new DiagnosticContext { Url = "http://192.168.1.1:8008/foo" }));
        sink.Entries[0].EndpointLabel.Should().Be("192.168.1.1:8008");
    }

    [Fact]
    [Trait("ac", "AC-8.4")]
    public void EndpointLabel_NullUrl_FallsBackToRemoteEndpoint()
    {
        var sink = new DiagnosticRingSink(new InlineUiDispatcher(), new StaticIdentityLookup(null));
        sink.Push(MakeEntry(new DiagnosticContext { Url = null, RemoteEndpoint = "192.168.1.42:54321" }));
        sink.Entries[0].EndpointLabel.Should().Be("192.168.1.42:54321");
    }

    [Fact]
    [Trait("ac", "AC-8.4")]
    public void EndpointLabel_NeitherUrlNorRemoteEndpoint_ResolvesToEmDash()
    {
        var sink = new DiagnosticRingSink(new InlineUiDispatcher(), new StaticIdentityLookup(null));
        sink.Push(MakeEntry(new DiagnosticContext()));
        sink.Entries[0].EndpointLabel.Should().Be("—");
    }

    [Fact]
    [Trait("ac", "AC-8.2")]
    public void EntriesProperty_IsSameInstanceAcrossPushes()
    {
        // AC-8.2: Story 5.1's DiagnosticsViewModel.Entries binds to the SAME collection
        // instance — no copy, no view layer. Verify by reference equality after pushes.
        var sink = new DiagnosticRingSink(new InlineUiDispatcher(), new StaticIdentityLookup(null));
        var before = sink.Entries;

        sink.Push(MakeEntry());
        sink.Push(MakeEntry());

        var after = sink.Entries;
        ReferenceEquals(before, after).Should().BeTrue();
    }

    // ─── Test doubles ──────────────────────────────────────────────────────

    private sealed class StaticIdentityLookup(string? name) : IDiagnosticIdentityLookup
    {
        public string? TryGetFriendlyName(Guid deviceUuid) => name;
    }

    private sealed class MutableIdentityLookup(string initialName) : IDiagnosticIdentityLookup
    {
        public string? Name { get; set; } = initialName;
        public string? TryGetFriendlyName(Guid deviceUuid) => Name;
    }
}
