namespace ohSpy.Core.Devices;

/// <summary>
/// UUID-keyed device registry (Decision 9). The external surface deliberately has NO
/// <c>DeviceAdded</c> event — <see cref="DeviceLoaded"/> fires exactly when an entry's
/// description is parsed, so ViewModels never see an entry before it is
/// <see cref="DescriptionFetchState.Loaded"/> (AC-9.3 / FR-047).
/// </summary>
public interface IDeviceRegistry
{
    /// <summary>Looks up an entry by UUID. Safe to call from any thread.</summary>
    bool TryGetEntry(Guid uuid, out RegistryEntry entry);

    /// <summary>Snapshot of entries whose <see cref="RegistryEntry.State"/> is Loaded only.</summary>
    IReadOnlyCollection<RegistryEntry> Loaded { get; }

    /// <summary>Total entry count across all states.</summary>
    int Count { get; }

    /// <summary>Raised on the UI thread when an entry reaches Loaded (FR-005).</summary>
    event Action<RegistryEntry> DeviceLoaded;

    /// <summary>Raised on the UI thread when a Loaded entry's display data changes (FR-054 trigger).</summary>
    event Action<RegistryEntry> DeviceUpdated;

    /// <summary>Raised on the UI thread when an entry is removed (byebye / prune / mismatch).</summary>
    event Action<Guid> DeviceRemoved;
}
