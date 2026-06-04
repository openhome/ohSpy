namespace ohSpy.Core.Tests.Devices;

using FluentAssertions;
using ohSpy.Core.Devices;
using ohSpy.Core.Tests.Fakes;

/// <summary>
/// Story 2.3 — <see cref="DeviceRegistry"/> (Decision 9). Uses <see cref="InlineUiDispatcher"/>
/// so posted mutations + event raises run synchronously. Covers the AC-9.3 surface (no
/// DeviceAdded; DeviceLoaded only on RaiseDeviceLoaded), AC-9.4 refresh-no-fetch, AC-9.5
/// re-discovery new instance, and AC-7.2 byebye cancellation.
/// </summary>
public sealed class DeviceRegistryTests
{
    private static readonly Uri Location = new("http://192.0.2.10:49152/desc.xml");

    private static DeviceRegistry NewRegistry() => new(new InlineUiDispatcher());

    private static string NewUdn() => $"uuid:{Guid.NewGuid()}";

    private static void Alive(DeviceRegistry r, string udn, CancellationToken adapterToken = default) =>
        r.OnAlive(udn, Location, DateTime.UtcNow, "Linn/1.0", TimeSpan.FromSeconds(1800), "1", "1", adapterToken);

    [Fact]
    [Trait("ac", "AC-9.3")]
    public void OnAlive_NewUuid_AddsPending_RaisesEntryNeedsFetch_AC93()
    {
        var registry = NewRegistry();
        var fetches = new List<RegistryEntry>();
        registry.EntryNeedsFetch += fetches.Add;
        var udn = NewUdn();

        Alive(registry, udn);

        registry.Count.Should().Be(1);
        registry.TryGetEntry(udn, out var entry).Should().BeTrue();
        entry.State.Should().Be(DescriptionFetchState.Pending);
        entry.AliveCount.Should().Be(1);
        fetches.Should().ContainSingle().Which.Should().BeSameAs(entry);
    }

    [Fact]
    [Trait("ac", "AC-9.4")]
    public void OnAlive_KnownUuid_RefreshesNoNewFetch_AC94()
    {
        var registry = NewRegistry();
        var fetchCount = 0;
        registry.EntryNeedsFetch += _ => fetchCount++;
        var udn = NewUdn();

        Alive(registry, udn);
        Alive(registry, udn); // second alive, same UUID

        registry.Count.Should().Be(1, "no new entry for a known UUID");
        registry.TryGetEntry(udn, out var entry).Should().BeTrue();
        entry.AliveCount.Should().Be(2);
        entry.State.Should().Be(DescriptionFetchState.Pending, "refresh does not transition");
        fetchCount.Should().Be(1, "no re-fetch for a known UUID (FR-043 cache invariant)");
    }

    [Fact]
    [Trait("ac", "AC-9.3")]
    public void Loaded_ReturnsOnlyLoadedEntries_CountIsAll_AC93()
    {
        var registry = NewRegistry();
        var a = NewUdn();
        var b = NewUdn();
        Alive(registry, a);
        Alive(registry, b);
        registry.TryGetEntry(a, out var entryA);
        entryA.MarkInFlight();
        entryA.MarkLoaded(StubDeviceDescriptionParser.Description(a));

        registry.Count.Should().Be(2, "Count covers all states");
        registry.Loaded.Should().ContainSingle().Which.Udn.Should().Be(a);
    }

    [Fact]
    [Trait("ac", "AC-9.3")]
    public void DeviceLoaded_FiresOnRaise_NotOnAdd_AC93()
    {
        var registry = NewRegistry();
        var loaded = new List<RegistryEntry>();
        registry.DeviceLoaded += loaded.Add;
        var udn = NewUdn();

        Alive(registry, udn);
        loaded.Should().BeEmpty("no DeviceAdded — VMs never see pre-Loaded entries");

        registry.TryGetEntry(udn, out var entry);
        entry.MarkInFlight();
        entry.MarkLoaded(StubDeviceDescriptionParser.Description(udn));
        registry.RaiseDeviceLoaded(entry);

        loaded.Should().ContainSingle().Which.Should().BeSameAs(entry);
    }

    [Fact]
    [Trait("ac", "AC-7.2")]
    public void OnByebye_CancelsCts_Removes_RaisesRemoved_AC72()
    {
        var registry = NewRegistry();
        var removed = new List<string>();
        registry.DeviceRemoved += removed.Add;
        var udn = NewUdn();
        Alive(registry, udn);
        registry.TryGetEntry(udn, out var entry);

        registry.OnByebye(udn);

        entry.DeviceToken.IsCancellationRequested.Should().BeTrue("byebye cancels the device CTS");
        registry.Count.Should().Be(0);
        removed.Should().ContainSingle().Which.Should().Be(udn);
    }

    [Fact]
    [Trait("ac", "AC-7.2")]
    public void OnByebye_UnknownUuid_NoOp_AC72()
    {
        var registry = NewRegistry();
        var removed = new List<string>();
        registry.DeviceRemoved += removed.Add;

        registry.OnByebye(NewUdn());

        removed.Should().BeEmpty();
        registry.Count.Should().Be(0);
    }

    [Fact]
    [Trait("ac", "AC-9.5")]
    public void Rediscovery_AfterByebye_CreatesNewInstance_AC95()
    {
        var registry = NewRegistry();
        var fetches = new List<RegistryEntry>();
        registry.EntryNeedsFetch += fetches.Add;
        var udn = NewUdn();

        Alive(registry, udn);
        registry.TryGetEntry(udn, out var first);
        registry.OnByebye(udn);
        Alive(registry, udn);
        registry.TryGetEntry(udn, out var second);

        second.Should().NotBeSameAs(first, "re-discovery creates a NEW entry instance");
        second.State.Should().Be(DescriptionFetchState.Pending);
        second.DeviceToken.IsCancellationRequested.Should().BeFalse("fresh DeviceCts");
        fetches.Should().HaveCount(2, "each new instance schedules a fresh fetch");
    }

    // ─── Story 5.2: Clear() (the atomic adapter-switch reset, FR-050 step 6) ───────

    [Fact]
    [Trait("ac", "AC-5.2.3")]
    public void Clear_RaisesDeviceRemovedPerUuid_DisposesEachCts_EmptiesRegistry()
    {
        var registry = NewRegistry();
        var removed = new List<string>();
        registry.DeviceRemoved += removed.Add;
        var a = NewUdn();
        var b = NewUdn();
        var c = NewUdn();
        Alive(registry, a);
        Alive(registry, b);
        Alive(registry, c);
        registry.TryGetEntry(a, out var entryA);
        registry.TryGetEntry(b, out var entryB);
        registry.TryGetEntry(c, out var entryC);

        registry.Clear();

        registry.Count.Should().Be(0, "Clear() empties the registry");
        removed.Should().BeEquivalentTo(new[] { a, b, c }, "one DeviceRemoved per UUID (byebye-identical cascade)");
        entryA.DeviceToken.IsCancellationRequested.Should().BeTrue("each DeviceCts cancelled + disposed");
        entryB.DeviceToken.IsCancellationRequested.Should().BeTrue();
        entryC.DeviceToken.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    [Trait("ac", "AC-5.2.3")]
    public void Clear_OnEmptyRegistry_IsNoOp()
    {
        var registry = NewRegistry();
        var removed = new List<string>();
        registry.DeviceRemoved += removed.Add;

        registry.Clear();

        removed.Should().BeEmpty();
        registry.Count.Should().Be(0);
    }

    [Fact]
    [Trait("ac", "AC-5.2.3")]
    public void Clear_IsIdempotent()
    {
        var registry = NewRegistry();
        var udn = NewUdn();
        Alive(registry, udn);
        var removed = new List<string>();
        registry.DeviceRemoved += removed.Add;

        registry.Clear();
        registry.Clear(); // second call: nothing left to remove

        removed.Should().ContainSingle().Which.Should().Be(udn);
        registry.Count.Should().Be(0);
    }

    [Fact]
    public void RaiseDeviceUpdated_FiresEvent()
    {
        // DeviceUpdated has no production trigger in Story 2.3 (FR-054 forward-looking);
        // prove the wiring directly.
        var registry = NewRegistry();
        var updated = new List<RegistryEntry>();
        registry.DeviceUpdated += updated.Add;
        var udn = NewUdn();
        Alive(registry, udn);
        registry.TryGetEntry(udn, out var entry);

        registry.RaiseDeviceUpdated(entry);

        updated.Should().ContainSingle().Which.Should().BeSameAs(entry);
    }

    // ─── Amendment A30 regression (c): a non-GUID UDN round-trips + de-dups (OrdinalIgnoreCase) ───

    [Fact]
    [Trait("ac", "AC-2.4.1")]
    public void Registry_RoundTripsAndDeDups_NonGuidUdn_OrdinalIgnoreCase()
    {
        var registry = NewRegistry();
        var removed = new List<string>();
        registry.DeviceRemoved += removed.Add;
        const string udn = "uuid:linn-ds-0001";

        Alive(registry, udn);
        Alive(registry, udn); // second alive, same opaque UDN

        registry.Count.Should().Be(1, "a non-GUID UDN de-dups like any other identity");
        registry.TryGetEntry(udn, out var entry).Should().BeTrue();
        entry.AliveCount.Should().Be(2, "both alives landed on the one entry");
        registry.TryGetEntry("UUID:LINN-DS-0001", out _).Should().BeTrue(
            "the registry keys OrdinalIgnoreCase (Amendment A30)");

        registry.OnByebye(udn);

        removed.Should().ContainSingle().Which.Should().Be(udn,
            "byebye raises DeviceRemoved with the verbatim UDN string");
        registry.Count.Should().Be(0);
    }
}
