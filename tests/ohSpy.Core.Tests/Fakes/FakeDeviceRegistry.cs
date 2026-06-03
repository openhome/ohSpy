namespace ohSpy.Core.Tests.Fakes;

using ohSpy.Core.Devices;

/// <summary>
/// Controllable <see cref="IDeviceRegistry"/> for PropertiesViewModel tests — lets a test raise
/// <see cref="DeviceRemoved"/> on demand (FR-037 banner). The lookup/collection surface is inert
/// (the VM snapshots its fields at construction and never queries the registry afterwards).
/// </summary>
internal sealed class FakeDeviceRegistry : IDeviceRegistry
{
    public event Action<RegistryEntry>? DeviceLoaded;
    public event Action<RegistryEntry>? DeviceUpdated;
    public event Action<Guid>? DeviceRemoved;

    public void RaiseDeviceRemoved(Guid uuid) => DeviceRemoved?.Invoke(uuid);

    // Unused by PropertiesViewModel — keep the events referenced so the compiler stays quiet.
    public void RaiseDeviceLoaded(RegistryEntry entry) => DeviceLoaded?.Invoke(entry);
    public void RaiseDeviceUpdated(RegistryEntry entry) => DeviceUpdated?.Invoke(entry);

    public bool TryGetEntry(Guid uuid, out RegistryEntry entry) { entry = null!; return false; }
    public IReadOnlyCollection<RegistryEntry> Loaded => Array.Empty<RegistryEntry>();
    public int Count => 0;
}
