namespace ohSpy.Core.ViewModels;

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ohSpy.Core.Devices;

public partial class DeviceNodeViewModel : ObservableObject, INodeViewModel
{
    private RegistryEntry _entry;

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

    public DeviceNodeViewModel(RegistryEntry entry)
    {
        _entry = entry;
        Children.Add(new LoadingPlaceholderViewModel()); // AC-A1.1: force expand chevron
        RefreshFrom(entry);
    }

    public Guid Uuid => _entry.Uuid;

    string INodeViewModel.Label => FriendlyName;

    // Called by DeviceTreeViewModel on DeviceUpdated to push new display values.
    internal void RefreshFrom(RegistryEntry entry)
    {
        _entry = entry;
        FriendlyName = entry.Description?.FriendlyName is { Length: > 0 } name
            ? name
            : $"uuid:{entry.Uuid}";
        SecondaryDetail = ComputeSecondaryDetail(entry);
    }

    // Story 2.6 wires service enumeration here.
    partial void OnIsExpandedChanged(bool value) { }

    internal void ReplaceWith(IReadOnlyList<INodeViewModel> newChildren)
    {
        Children.Clear();
        foreach (var child in newChildren)
            Children.Add(child);
        // Clear+Add emits Reset; acceptable for placeholder->real-children swap (A1 atomic rule).
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