namespace ohSpy.Core.ViewModels;

using ohSpy.Core.Collections;
using ohSpy.Core.Devices;
using ohSpy.Core.Threading;

public sealed class DeviceTreeViewModel : IDisposable
{
    private readonly IDeviceRegistry _registry;
    private readonly IUiDispatcher _ui;
    private int _disposed;

    public IdentityKeyedSortedCollection<Guid, DeviceNodeViewModel> Devices { get; }

    public DeviceTreeViewModel(IDeviceRegistry registry, IUiDispatcher ui)
    {
        _registry = registry;
        _ui = ui;
        Devices = new IdentityKeyedSortedCollection<Guid, DeviceNodeViewModel>(
            vm => vm.Uuid,
            DeviceNodeComparer.Instance);

        registry.DeviceLoaded  += OnDeviceLoaded;
        registry.DeviceUpdated += OnDeviceUpdated;
        registry.DeviceRemoved += OnDeviceRemoved;
    }

    // Treat a duplicate Loaded for a known UUID as an update rather than letting
    // IdentityKeyedSortedCollection.Add throw ArgumentException on the UI thread
    // (symmetric with OnDeviceUpdated). The registry's Loaded state is terminal so a
    // true duplicate is unlikely today, but the guard removes the unhandled-throw path.
    private void OnDeviceLoaded(RegistryEntry entry) =>
        _ui.Post(() =>
        {
            if (Devices.TryGetItem(entry.Uuid, out var existing))
            {
                existing.RefreshFrom(entry);
                Devices.Update(existing);
            }
            else
            {
                Devices.Add(new DeviceNodeViewModel(entry));
            }
        });

    private void OnDeviceUpdated(RegistryEntry entry)
    {
        _ui.Post(() =>
        {
            if (Devices.TryGetItem(entry.Uuid, out var vm))
            {
                vm.RefreshFrom(entry);
                Devices.Update(vm); // re-sort if FriendlyName changed
            }
        });
    }

    private void OnDeviceRemoved(Guid uuid) =>
        _ui.Post(() => Devices.Remove(uuid));

    /// <summary>
    /// Unsubscribes from the registry's events. The registry is a long-lived singleton;
    /// without this the handlers would root this VM for the registry's lifetime. Idempotent.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        _registry.DeviceLoaded  -= OnDeviceLoaded;
        _registry.DeviceUpdated -= OnDeviceUpdated;
        _registry.DeviceRemoved -= OnDeviceRemoved;
    }
}

internal sealed class DeviceNodeComparer : IComparer<DeviceNodeViewModel>
{
    public static readonly DeviceNodeComparer Instance = new();
    private DeviceNodeComparer() { }

    public int Compare(DeviceNodeViewModel? x, DeviceNodeViewModel? y)
    {
        if (x is null && y is null) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        // Primary: case-insensitive FriendlyName (FR-054).
        int nameCmp = string.Compare(x.FriendlyName, y.FriendlyName,
            StringComparison.OrdinalIgnoreCase);
        if (nameCmp != 0) return nameCmp;

        // Tiebreak: ordinal UUID string (stable for equal friendly names).
        return string.Compare(x.Uuid.ToString(), y.Uuid.ToString(),
            StringComparison.Ordinal);
    }
}
