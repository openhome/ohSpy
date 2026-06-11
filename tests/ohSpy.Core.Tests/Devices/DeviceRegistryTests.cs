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

    // ─── Story 5.3: PruneNotSeenSince (the rescan prune, FR-023) ──────────────────

    [Fact]
    [Trait("ac", "AC-5.3.8")]
    [Trait("fr", "FR-023")]
    public void PruneNotSeenSince_RemovesOnlyEntriesNotSeenSinceEpoch_FR023()
    {
        var registry = NewRegistry();
        var removed = new List<string>();
        registry.DeviceRemoved += removed.Add;

        // Two stale devices (seen before the epoch), two fresh (seen at/after the epoch).
        var stale1 = NewUdn();
        var stale2 = NewUdn();
        registry.OnAlive(stale1, Location, DateTime.UtcNow.AddSeconds(-10), "S", null, null, null, CancellationToken.None);
        registry.OnAlive(stale2, Location, DateTime.UtcNow.AddSeconds(-10), "S", null, null, null, CancellationToken.None);

        var epoch = DateTime.UtcNow;

        var fresh1 = NewUdn();
        var fresh2 = NewUdn();
        registry.OnAlive(fresh1, Location, epoch.AddSeconds(1), "S", null, null, null, CancellationToken.None);
        registry.OnAlive(fresh2, Location, epoch.AddSeconds(1), "S", null, null, null, CancellationToken.None);
        registry.TryGetEntry(stale1, out var staleEntry1);

        var pruned = registry.PruneNotSeenSince(epoch);

        pruned.Should().Be(2, "only the two entries with LastSeenUtc < epoch are pruned");
        removed.Should().BeEquivalentTo(new[] { stale1, stale2 }, "one DeviceRemoved per pruned UDN");
        staleEntry1.DeviceToken.IsCancellationRequested.Should().BeTrue("the pruned entry's DeviceCts is cancelled");
        registry.Count.Should().Be(2, "the fresh entries survive");
        registry.TryGetEntry(fresh1, out _).Should().BeTrue();
        registry.TryGetEntry(fresh2, out _).Should().BeTrue();
    }

    [Fact]
    [Trait("ac", "AC-5.3.8")]
    [Trait("fr", "FR-023")]
    public void PruneNotSeenSince_RefreshedEntrySurvives_FR023()
    {
        // A device seen before the epoch but refreshed AFTER it (a rescan response) must survive — the
        // prune rides RefreshSsdpMetadata's LastSeenUtc update through OnAlive.
        var registry = NewRegistry();
        var udn = NewUdn();
        registry.OnAlive(udn, Location, DateTime.UtcNow.AddSeconds(-10), "S", null, null, null, CancellationToken.None);

        var epoch = DateTime.UtcNow;
        registry.OnAlive(udn, Location, epoch.AddSeconds(1), "S", null, null, null, CancellationToken.None); // "responded"

        var pruned = registry.PruneNotSeenSince(epoch);

        pruned.Should().Be(0, "the refreshed (responded) entry has LastSeenUtc ≥ epoch");
        registry.Count.Should().Be(1);
    }

    [Fact]
    [Trait("ac", "AC-5.3.8")]
    [Trait("fr", "FR-023")]
    public void PruneNotSeenSince_EmptyRegistry_ReturnsZero_FR023()
    {
        var registry = NewRegistry();
        var removed = new List<string>();
        registry.DeviceRemoved += removed.Add;

        registry.PruneNotSeenSince(DateTime.UtcNow).Should().Be(0);

        removed.Should().BeEmpty();
        registry.Count.Should().Be(0);
    }

    [Fact]
    [Trait("ac", "AC-5.3.8")]
    [Trait("fr", "FR-023")]
    public void PruneNotSeenSince_IsIdempotent_FR023()
    {
        var registry = NewRegistry();
        var udn = NewUdn();
        registry.OnAlive(udn, Location, DateTime.UtcNow.AddSeconds(-10), "S", null, null, null, CancellationToken.None);
        var removed = new List<string>();
        registry.DeviceRemoved += removed.Add;
        var epoch = DateTime.UtcNow;

        registry.PruneNotSeenSince(epoch).Should().Be(1);
        registry.PruneNotSeenSince(epoch).Should().Be(0, "second prune finds nothing — no double DeviceRemoved");

        removed.Should().ContainSingle().Which.Should().Be(udn);
        registry.Count.Should().Be(0);
    }

    // ─── Story 2.11 / FR-056: ExpireOlderThan (the automatic per-entry-lease expiry sweep) ──────────

    private static readonly TimeSpan DefaultLease = TimeSpan.FromSeconds(1800);
    private static readonly TimeSpan Jitter = TimeSpan.FromSeconds(5);

    [Fact]
    [Trait("fr", "FR-056")]
    public void ExpireOlderThan_DevicePastItsLease_EvictedWithByebyeCascade_FR056()
    {
        var registry = NewRegistry();
        var removed = new List<string>();
        registry.DeviceRemoved += removed.Add;
        var udn = NewUdn();

        var lastSeen = DateTime.UtcNow;
        var lease = TimeSpan.FromSeconds(100);
        registry.OnAlive(udn, Location, lastSeen, "S", lease, null, null, CancellationToken.None);
        registry.TryGetEntry(udn, out var entry);

        // now is past LastSeenUtc + lease + jitter → expired.
        var now = lastSeen + lease + Jitter + TimeSpan.FromSeconds(1);
        var evicted = registry.ExpireOlderThan(now, DefaultLease, Jitter);

        evicted.Select(e => e.Udn).Should().BeEquivalentTo(new[] { udn }, "the device past its lease is evicted, UDN returned");
        evicted.Single().MaxAge.Should().Be(lease, "the evicted device's advertised max-age is returned for the per-device diagnostic");
        removed.Should().ContainSingle().Which.Should().Be(udn, "byebye-identical cascade raises DeviceRemoved");
        entry.DeviceToken.IsCancellationRequested.Should().BeTrue("the evicted entry's DeviceCts is cancelled (AC-7.2)");
        registry.Count.Should().Be(0);
    }

    [Fact]
    [Trait("fr", "FR-056")]
    public void ExpireOlderThan_RefreshedEntry_ResetsLease_Survives_FR056()
    {
        var registry = NewRegistry();
        var udn = NewUdn();
        var lease = TimeSpan.FromSeconds(100);

        var firstSeen = DateTime.UtcNow;
        registry.OnAlive(udn, Location, firstSeen, "S", lease, null, null, CancellationToken.None);

        // A refreshing alive bumps LastSeenUtc to a later time (the device re-advertised within its lease).
        var refreshedAt = firstSeen + TimeSpan.FromSeconds(90);
        registry.OnAlive(udn, Location, refreshedAt, "S", lease, null, null, CancellationToken.None);

        // "now" would have expired the ORIGINAL lease but not the refreshed one.
        var now = firstSeen + lease + Jitter + TimeSpan.FromSeconds(1);
        var evicted = registry.ExpireOlderThan(now, DefaultLease, Jitter);

        evicted.Should().BeEmpty("the refresh reset the lease (now ≤ refreshedAt + lease + jitter)");
        registry.Count.Should().Be(1, "a re-advertising device survives the sweep");
    }

    [Fact]
    [Trait("fr", "FR-056")]
    public void ExpireOlderThan_NullMaxAge_UsesDefaultLease_FR056()
    {
        var registry = NewRegistry();
        var udn = NewUdn();

        var lastSeen = DateTime.UtcNow;
        registry.OnAlive(udn, Location, lastSeen, "S", maxAge: null, null, null, CancellationToken.None);

        // Just inside the default lease → survives.
        var withinDefault = lastSeen + DefaultLease; // == lease edge, not yet past lease + jitter
        registry.ExpireOlderThan(withinDefault, DefaultLease, Jitter).Should().BeEmpty(
            "a null max-age entry survives within the 1800s default lease");

        // Past the default lease + jitter → evicted.
        var pastDefault = lastSeen + DefaultLease + Jitter + TimeSpan.FromSeconds(1);
        var evictedDefault = registry.ExpireOlderThan(pastDefault, DefaultLease, Jitter);
        evictedDefault.Select(e => e.Udn).Should().BeEquivalentTo(new[] { udn },
            "a null max-age entry still expires via the default lease (never lives forever)");
        evictedDefault.Single().MaxAge.Should().BeNull("a device that advertised no CACHE-CONTROL returns a null max-age");
        registry.Count.Should().Be(0);
    }

    [Fact]
    [Trait("fr", "FR-056")]
    public void ExpireOlderThan_JitterEdge_JustInsideSurvives_JustPastEvicts_FR056()
    {
        var registry = NewRegistry();
        var udn = NewUdn();
        var lease = TimeSpan.FromSeconds(100);
        var lastSeen = DateTime.UtcNow;
        registry.OnAlive(udn, Location, lastSeen, "S", lease, null, null, CancellationToken.None);

        // Exactly at LastSeenUtc + lease + jitter → NOT past (strict >) → survives.
        var atEdge = lastSeen + lease + Jitter;
        registry.ExpireOlderThan(atEdge, DefaultLease, Jitter).Should().BeEmpty(
            "the device at the exact lease+jitter edge survives (strict > comparison)");
        registry.Count.Should().Be(1);

        // One tick past the edge → evicted.
        var pastEdge = atEdge + TimeSpan.FromTicks(1);
        registry.ExpireOlderThan(pastEdge, DefaultLease, Jitter).Select(e => e.Udn).Should().BeEquivalentTo(new[] { udn });
        registry.Count.Should().Be(0);
    }

    [Fact]
    [Trait("fr", "FR-056")]
    public void ExpireOlderThan_IsIdempotent_NoDoubleRemoved_FR056()
    {
        var registry = NewRegistry();
        var removed = new List<string>();
        registry.DeviceRemoved += removed.Add;
        var udn = NewUdn();
        var lease = TimeSpan.FromSeconds(100);
        var lastSeen = DateTime.UtcNow;
        registry.OnAlive(udn, Location, lastSeen, "S", lease, null, null, CancellationToken.None);

        var now = lastSeen + lease + Jitter + TimeSpan.FromSeconds(1);
        registry.ExpireOlderThan(now, DefaultLease, Jitter).Select(e => e.Udn).Should().BeEquivalentTo(new[] { udn });
        registry.ExpireOlderThan(now, DefaultLease, Jitter).Should().BeEmpty(
            "second sweep finds nothing — no double DeviceRemoved (shared RemoveCore.TryRemove)");

        removed.Should().ContainSingle().Which.Should().Be(udn);
        registry.Count.Should().Be(0);
    }

    [Fact]
    [Trait("fr", "FR-056")]
    public void ExpireOlderThan_EmptyRegistry_ReturnsEmpty_FR056()
    {
        var registry = NewRegistry();
        var removed = new List<string>();
        registry.DeviceRemoved += removed.Add;

        registry.ExpireOlderThan(DateTime.UtcNow, DefaultLease, Jitter).Should().BeEmpty();

        removed.Should().BeEmpty();
        registry.Count.Should().Be(0);
    }

    [Fact]
    [Trait("fr", "FR-056")]
    public void ExpireOlderThan_OnlyExpiredEvicted_LiveDevicesSurvive_FR056()
    {
        var registry = NewRegistry();
        var lease = TimeSpan.FromSeconds(100);
        var baseTime = DateTime.UtcNow;

        var stale = NewUdn();
        var live = NewUdn();
        registry.OnAlive(stale, Location, baseTime, "S", lease, null, null, CancellationToken.None);
        registry.OnAlive(live, Location, baseTime + TimeSpan.FromSeconds(80), "S", lease, null, null, CancellationToken.None);

        // now expires `stale` (seen at baseTime) but not `live` (seen 80s later).
        var now = baseTime + lease + Jitter + TimeSpan.FromSeconds(1);
        var evicted = registry.ExpireOlderThan(now, DefaultLease, Jitter);

        evicted.Select(e => e.Udn).Should().BeEquivalentTo(new[] { stale }, "only the entry past its lease is evicted");
        registry.Count.Should().Be(1);
        registry.TryGetEntry(live, out _).Should().BeTrue("the live device survives");
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
