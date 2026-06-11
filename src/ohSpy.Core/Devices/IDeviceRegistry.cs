namespace ohSpy.Core.Devices;

/// <summary>
/// UDN-keyed device registry (Decision 9 / Amendment A30). The external surface deliberately has NO
/// <c>DeviceAdded</c> event — <see cref="DeviceLoaded"/> fires exactly when an entry's
/// description is parsed, so ViewModels never see an entry before it is
/// <see cref="DescriptionFetchState.Loaded"/> (AC-9.3 / FR-047).
/// </summary>
public interface IDeviceRegistry
{
    /// <summary>Looks up an entry by UDN (string, OrdinalIgnoreCase). Safe to call from any thread.</summary>
    bool TryGetEntry(string udn, out RegistryEntry entry);

    /// <summary>Snapshot of entries whose <see cref="RegistryEntry.State"/> is Loaded only.</summary>
    IReadOnlyCollection<RegistryEntry> Loaded { get; }

    /// <summary>Total entry count across all states.</summary>
    int Count { get; }

    /// <summary>Raised on the UI thread when an entry reaches Loaded (FR-005).</summary>
    event Action<RegistryEntry> DeviceLoaded;

    /// <summary>Raised on the UI thread when a Loaded entry's display data changes (FR-054 trigger).</summary>
    event Action<RegistryEntry> DeviceUpdated;

    /// <summary>Raised on the UI thread when an entry is removed (byebye / prune / mismatch / clear).</summary>
    event Action<string> DeviceRemoved;

    /// <summary>
    /// Removes EVERY entry (the Story 5.2 atomic adapter-switch reset, FR-050 step 6). Runs on the UI
    /// thread: for each current UDN it cancels + disposes the entry's <c>DeviceCts</c> and raises
    /// <see cref="DeviceRemoved"/> (the same <c>RemoveCore</c> cascade as byebye, so the tree drops rows
    /// and open popups flip to their FR-037 device-gone banners), then empties the registry. Idempotent
    /// and safe on an empty registry. The SSDP log clear is a SEPARATE call
    /// (<c>SsdpLogViewModel.Clear()</c>, Story 2.7).
    /// </summary>
    void Clear();

    /// <summary>
    /// Story 5.3 rescan prune (FR-023). Removes every entry whose <see cref="RegistryEntry.LastSeenUtc"/>
    /// is STRICTLY before <paramref name="epochUtc"/> — i.e. any device that neither responded to the
    /// rescan M-SEARCH nor announced an unsolicited alive since the epoch was stamped. Runs on the UI
    /// thread: like <see cref="Clear"/> it snapshots the current UDNs FIRST, then removes each stale one
    /// via the shared byebye-identical cascade (cancel + dispose its <c>DeviceCts</c> and raise
    /// <see cref="DeviceRemoved"/> per UDN, so the tree drops the row and open popups flip to their
    /// FR-037 device-gone banners). Responders / in-window alives have refreshed <c>LastSeenUtc</c> (via
    /// <c>OnAlive</c>) so they survive. A device already removed by a byebye during the window is simply
    /// not found — the prune is idempotent (no double <see cref="DeviceRemoved"/>). Returns the number of
    /// entries pruned. Safe (returns 0) on an empty registry.
    /// </summary>
    int PruneNotSeenSince(DateTime epochUtc);

    /// <summary>
    /// FR-056 / Amendment A33 expiry sweep — the AUTOMATIC per-entry-lease cousin of
    /// <see cref="PruneNotSeenSince"/> (which keys on a single global epoch; this keys on each entry's
    /// own <c>CACHE-CONTROL</c> lease). Removes every entry whose lease has lapsed without a refreshing
    /// alive — i.e. <paramref name="nowUtc"/> is past <see cref="RegistryEntry.LastSeenUtc"/> + lease +
    /// <paramref name="jitter"/>, where lease = <see cref="RegistryEntry.CacheControlMaxAge"/> (or
    /// <paramref name="defaultLease"/> when the latest alive omitted <c>max-age</c>). Runs on the UI
    /// thread: like <see cref="Clear"/> / <see cref="PruneNotSeenSince"/> it snapshots the current UDNs
    /// FIRST, then removes each expired one via the shared byebye-identical cascade (cancel + dispose its
    /// <c>DeviceCts</c> and raise <see cref="DeviceRemoved"/> per UDN — so the tree drops the row, open
    /// popups flip to their FR-037 device-gone banners, and any in-flight description/SCPD fetch is
    /// cancelled). A refreshing alive bumped <c>LastSeenUtc</c> (via <c>OnAlive</c>) so a live device
    /// survives. Idempotent with byebye / Rescan / Clear (shared <c>RemoveCore.TryRemove</c> — an
    /// already-removed UDN raises no second <see cref="DeviceRemoved"/>). Returns the evicted devices —
    /// each UDN with the <c>CACHE-CONTROL</c> max-age it had advertised (null when it relied on the
    /// default lease) — so the caller can emit one per-device-accurate <c>Ssdp.Expired</c> diagnostic;
    /// empty on an empty registry. The <paramref name="nowUtc"/> is supplied by the caller (the registry
    /// holds no clock — it stays pure + instantly testable).
    /// </summary>
    IReadOnlyList<(string Udn, TimeSpan? MaxAge)> ExpireOlderThan(DateTime nowUtc, TimeSpan defaultLease, TimeSpan jitter);
}
