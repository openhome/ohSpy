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
}
