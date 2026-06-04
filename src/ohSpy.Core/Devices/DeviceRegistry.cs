namespace ohSpy.Core.Devices;

using System.Collections.Concurrent;
using System.Linq;
using ohSpy.Core.Threading;

/// <summary>
/// UDN-keyed device registry (Decision 9 / Amendment A30 — string identity, OrdinalIgnoreCase).
/// Mutations (<see cref="OnAlive"/>,
/// <see cref="OnByebye"/>, <see cref="Remove"/>, the <c>Raise*</c> helpers) run on the UI
/// thread (callers marshal via <see cref="IUiDispatcher.Post"/>; the mutators assert it).
/// <para>
/// The backing store is a <see cref="ConcurrentDictionary{TKey,TValue}"/> because
/// <see cref="TryGetEntry"/> is read off the UI thread by <c>RegistryIdentityLookup</c> →
/// <c>DiagnosticRingSink.Push</c> (which resolves identity on the emitting thread — often a
/// background fetch task). A plain Dictionary read concurrent with a UI-thread write is a
/// data race; the concurrent dictionary guards the structure for that cross-thread read.
/// </para>
/// <para>
/// This type deliberately does NOT depend on <c>IDiagnosticEmitter</c> — that would form a
/// DI cycle (Emitter → RingSink → IDiagnosticIdentityLookup → DeviceRegistry → Emitter).
/// All diagnostics in the discovery pipeline are the <c>EagerDescriptionDispatcher</c>'s job.
/// </para>
/// </summary>
internal sealed class DeviceRegistry(IUiDispatcher ui) : IDeviceRegistry
{
    private readonly ConcurrentDictionary<string, RegistryEntry> _entries =
        new(StringComparer.OrdinalIgnoreCase);

    public event Action<RegistryEntry>? DeviceLoaded;
    public event Action<RegistryEntry>? DeviceUpdated;
    public event Action<string>? DeviceRemoved;

    /// <summary>
    /// Internal coordinator signal raised when a NEW entry is created. The
    /// <c>EagerDescriptionDispatcher</c> subscribes to schedule the fetch — this breaks the
    /// registry↔dispatcher dependency cycle (the registry never references the dispatcher).
    /// Not on <see cref="IDeviceRegistry"/> so the external surface stays clean (no DeviceAdded).
    /// </summary>
    internal event Action<RegistryEntry>? EntryNeedsFetch;

    public bool TryGetEntry(string udn, out RegistryEntry entry) => _entries.TryGetValue(udn, out entry!);

    public int Count => _entries.Count;

    public IReadOnlyCollection<RegistryEntry> Loaded =>
        _entries.Values.Where(e => e.State == DescriptionFetchState.Loaded).ToArray();

    /// <summary>
    /// Handles an alive announcement (call surface is DiscoveryService in Story 2.4). A new
    /// UDN creates a Pending entry and raises <see cref="EntryNeedsFetch"/>; a known UDN
    /// only refreshes metadata — no re-fetch (AC-9.4 / FR-043 cache invariant).
    /// </summary>
    internal void OnAlive(string udn, Uri location, DateTime nowUtc, string? server,
        TimeSpan? maxAge, string? bootId, string? configId, CancellationToken adapterToken)
    {
        ui.AssertOnUiThread();

        if (_entries.TryGetValue(udn, out var existing))
        {
            existing.RefreshSsdpMetadata(nowUtc, server, maxAge, bootId, configId); // AC-9.4
            return;
        }

        var entry = new RegistryEntry(udn, location, nowUtc, adapterToken);
        entry.RefreshSsdpMetadata(nowUtc, server, maxAge, bootId, configId); // seed metadata; AliveCount 0→1
        _entries[udn] = entry;
        EntryNeedsFetch?.Invoke(entry); // dispatcher schedules FetchAsync
    }

    /// <summary>Handles a byebye (FR-008): cancels the device's in-flight fetch (AC-7.2) and removes it.</summary>
    internal void OnByebye(string udn)
    {
        ui.AssertOnUiThread();
        RemoveCore(udn);
    }

    /// <summary>Removes an entry (the dispatcher's mismatched-root path, AC-9.6). Idempotent.</summary>
    internal void Remove(string udn)
    {
        ui.AssertOnUiThread();
        RemoveCore(udn);
    }

    /// <summary>Raises <see cref="DeviceLoaded"/> (called by the dispatcher after MarkLoaded).</summary>
    internal void RaiseDeviceLoaded(RegistryEntry entry)
    {
        ui.AssertOnUiThread();
        DeviceLoaded?.Invoke(entry);
    }

    /// <summary>Raises <see cref="DeviceUpdated"/> (FR-054). No production trigger in Story 2.3.</summary>
    internal void RaiseDeviceUpdated(RegistryEntry entry)
    {
        ui.AssertOnUiThread();
        DeviceUpdated?.Invoke(entry);
    }

    /// <summary>
    /// Story 5.2 atomic adapter-switch reset (FR-050 step 6). Snapshots the current UDNs FIRST, then
    /// removes each via the shared <see cref="RemoveCore"/> cascade — so a <see cref="DeviceRemoved"/>
    /// handler that re-reads the registry sees a consistent (shrinking) state and the removal semantics
    /// match byebye exactly (the popups' FR-037 path). Idempotent; a no-op on an empty registry.
    /// </summary>
    public void Clear()
    {
        ui.AssertOnUiThread();

        // Snapshot the keys before mutating so the per-UDN DeviceRemoved handlers (which may re-read
        // the registry) observe a consistent state, and so we never iterate a collection we mutate.
        var udns = _entries.Keys.ToArray();
        foreach (var udn in udns)
        {
            RemoveCore(udn); // cancel + dispose DeviceCts + raise DeviceRemoved (byebye-identical)
        }
    }

    private void RemoveCore(string udn)
    {
        if (_entries.TryRemove(udn, out var entry))
        {
            entry.DeviceCts.Cancel();   // AC-7.2: cancels THIS device's in-flight fetch only
            entry.DeviceCts.Dispose();  // release the linked-token callback on the adapter CTS
            DeviceRemoved?.Invoke(udn);
        }
    }
}
