namespace ohSpy.Core.Devices;

using System.Collections.Concurrent;
using System.Linq;
using ohSpy.Core.Threading;

/// <summary>
/// UUID-keyed device registry (Decision 9). Mutations (<see cref="OnAlive"/>,
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
    private readonly ConcurrentDictionary<Guid, RegistryEntry> _entries = new();

    public event Action<RegistryEntry>? DeviceLoaded;
    public event Action<RegistryEntry>? DeviceUpdated;
    public event Action<Guid>? DeviceRemoved;

    /// <summary>
    /// Internal coordinator signal raised when a NEW entry is created. The
    /// <c>EagerDescriptionDispatcher</c> subscribes to schedule the fetch — this breaks the
    /// registry↔dispatcher dependency cycle (the registry never references the dispatcher).
    /// Not on <see cref="IDeviceRegistry"/> so the external surface stays clean (no DeviceAdded).
    /// </summary>
    internal event Action<RegistryEntry>? EntryNeedsFetch;

    public bool TryGetEntry(Guid uuid, out RegistryEntry entry) => _entries.TryGetValue(uuid, out entry!);

    public int Count => _entries.Count;

    public IReadOnlyCollection<RegistryEntry> Loaded =>
        _entries.Values.Where(e => e.State == DescriptionFetchState.Loaded).ToArray();

    /// <summary>
    /// Handles an alive announcement (call surface is DiscoveryService in Story 2.4). A new
    /// UUID creates a Pending entry and raises <see cref="EntryNeedsFetch"/>; a known UUID
    /// only refreshes metadata — no re-fetch (AC-9.4 / FR-043 cache invariant).
    /// </summary>
    internal void OnAlive(Guid uuid, Uri location, DateTime nowUtc, string? server,
        TimeSpan? maxAge, string? bootId, string? configId, CancellationToken adapterToken)
    {
        ui.AssertOnUiThread();

        if (_entries.TryGetValue(uuid, out var existing))
        {
            existing.RefreshSsdpMetadata(nowUtc, server, maxAge, bootId, configId); // AC-9.4
            return;
        }

        var entry = new RegistryEntry(uuid, location, nowUtc, adapterToken);
        entry.RefreshSsdpMetadata(nowUtc, server, maxAge, bootId, configId); // seed metadata; AliveCount 0→1
        _entries[uuid] = entry;
        EntryNeedsFetch?.Invoke(entry); // dispatcher schedules FetchAsync
    }

    /// <summary>Handles a byebye (FR-008): cancels the device's in-flight fetch (AC-7.2) and removes it.</summary>
    internal void OnByebye(Guid uuid)
    {
        ui.AssertOnUiThread();
        RemoveCore(uuid);
    }

    /// <summary>Removes an entry (the dispatcher's mismatched-root path, AC-9.6). Idempotent.</summary>
    internal void Remove(Guid uuid)
    {
        ui.AssertOnUiThread();
        RemoveCore(uuid);
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

    private void RemoveCore(Guid uuid)
    {
        if (_entries.TryRemove(uuid, out var entry))
        {
            entry.DeviceCts.Cancel();   // AC-7.2: cancels THIS device's in-flight fetch only
            entry.DeviceCts.Dispose();  // release the linked-token callback on the adapter CTS
            DeviceRemoved?.Invoke(uuid);
        }
    }
}
