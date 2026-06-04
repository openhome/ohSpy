namespace ohSpy.Core.ViewModels;

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ohSpy.Core.Devices;
using ohSpy.Core.Models;

public partial class DeviceNodeViewModel : ObservableObject, INodeViewModel
{
    private RegistryEntry _entry;
    private readonly NodeServices _services;
    private int _servicesBuilt; // 0 = not built, 1 = built (Interlocked guard — AC-2.6.2)

    [ObservableProperty] private string _friendlyName = "";
    [ObservableProperty] private string _secondaryDetail = "";
    [ObservableProperty] private bool _isExpanded;

    public NodeKind Kind => NodeKind.Device;

    // Instance property (not static) required for x:Bind in WinUI DataTemplate.
    // U+E703 = Segoe MDL2 Assets "Network" glyph. Story 2.6 adds Service/Action glyphs.
#pragma warning disable CA1822
    public string KindGlyph => "";
#pragma warning restore CA1822

    public ObservableCollection<INodeViewModel> Children { get; } = [];

    public DeviceNodeViewModel(RegistryEntry entry, NodeServices services)
    {
        _entry = entry;
        _services = services;
        Children.Add(new LoadingPlaceholderViewModel()); // AC-A1.1: force expand chevron
        RefreshFrom(entry);
    }

    public string Udn => _entry.Udn;

    string INodeViewModel.Label => FriendlyName;

    // Called by DeviceTreeViewModel on DeviceUpdated to push new display values.
    internal void RefreshFrom(RegistryEntry entry)
    {
        _entry = entry;
        FriendlyName = entry.Description?.FriendlyName is { Length: > 0 } name
            ? name
            : entry.Udn;
        SecondaryDetail = ComputeSecondaryDetail(entry);
    }

    // AC-2.6.2: first expand swaps the placeholder for the (already-loaded, flattened)
    // service list — synchronous, no HTTP. Build once: re-expand must NOT rebuild (that would
    // emit a second Reset and collapse any expanded service subtrees — the Story 2.5 deferral).
    partial void OnIsExpandedChanged(bool value)
    {
        if (!value) return;
        if (Interlocked.Exchange(ref _servicesBuilt, 1) == 1) return;

        var services = _entry.Description?.Services ?? [];
        // Sort services alphabetically by URN domain, then service name — the device-XML order is
        // arbitrary and confusing to operators; a stable, readable order is easier to scan.
        var nodes = services
            .Select(s => (svc: s, key: ServiceSortKey(s)))
            .OrderBy(x => x.key.Domain, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.key.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.svc.ServiceType, StringComparer.OrdinalIgnoreCase) // version/raw tiebreak — deterministic
            .Select(x => (INodeViewModel)new ServiceNodeViewModel(
                x.svc, _entry.LocationUrl, _entry.Udn, _entry, _services, _entry.DeviceToken))
            .ToList();
        ReplaceWith(nodes); // single Reset — AC-A1.4
    }

    internal void ReplaceWith(IReadOnlyList<INodeViewModel> newChildren)
    {
        Children.Clear();
        foreach (var child in newChildren)
            Children.Add(child);
        // Clear+Add emits Reset; acceptable for placeholder->real-children swap (A1 atomic rule).
    }

    // AC-2.8.2/2.8.3: open the device description (LocationUrl) in the default browser.
    // Whitelist + warn-on-failure live in the shared BrowserLaunch helper. Synchronous void —
    // shell-execute is fire-and-forget (AC-2.8.6), no async readback.
    [RelayCommand]
    private void FetchXml() =>
        BrowserLaunch.OpenInDefaultBrowser(
            _entry.LocationUrl, _services.Launcher, _services.Diag, _entry.Udn);

    // AC-2.9.7: open the read-only Properties window (Story 2.9). The window construction lives in
    // the App-side IPropertiesLauncher impl (a Core VM can't new up a WinUI Window — Pattern 2);
    // this command just hands off the entry. Synchronous fire-and-forget (matches FetchXml).
    [RelayCommand]
    private void OpenProperties() => _services.PropertiesLauncher.OpenProperties(_entry);

    // Sort key for the service list: (URN domain, service name). A serviceType is shaped
    // "urn:<domain>:service:<name>:<version>" (e.g. "urn:schemas-upnp-org:service:AVTransport:1"
    // or "urn:av-openhome-org:service:Product:1"). A non-conforming serviceType (no ":service:"
    // marker) sorts by the whole string under an empty domain, so it lands deterministically.
    private static (string Domain, string Name) ServiceSortKey(ServiceDescription service)
    {
        var type = service.ServiceType;
        const string marker = ":service:";
        var idx = type.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return ("", type);

        var domain = type.StartsWith("urn:", StringComparison.OrdinalIgnoreCase)
            ? type[4..idx]
            : type[..idx];
        var tail = type[(idx + marker.Length)..];
        var colon = tail.IndexOf(':');
        var name = colon >= 0 ? tail[..colon] : tail;
        return (domain, name);
    }

    // FR-051: secondary detail is "<deviceTypeTail> <U+00B7 middle-dot> <host>:<port>".
    // Degenerate device metadata is guarded: an empty type tail drops the separator, and an
    // unresolvable port (Uri.Port == -1) drops the ":port" so the UI never shows "host:-1".
    private static string ComputeSecondaryDetail(RegistryEntry entry)
    {
        var deviceType = entry.Description?.DeviceType ?? "";
        const string deviceMarker = ":device:";
        var idx = deviceType.IndexOf(deviceMarker, StringComparison.OrdinalIgnoreCase);
        var tail = idx >= 0 ? deviceType[(idx + deviceMarker.Length)..] : deviceType;

        var url = entry.LocationUrl;
        var hostPort = url.Port >= 0 ? $"{url.Host}:{url.Port}" : url.Host;

        return tail.Length > 0 ? $"{tail} · {hostPort}" : hostPort;
    }
}