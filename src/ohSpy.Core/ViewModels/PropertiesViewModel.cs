namespace ohSpy.Core.ViewModels;

using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ohSpy.Core.Devices;
using ohSpy.Core.Diagnostics;
using ohSpy.Core.Shell;

/// <summary>
/// Read-only Properties popup ViewModel (FR-052). Snapshots EVERY display field from the
/// <see cref="RegistryEntry"/> at construction so the popup survives the device leaving the
/// network (FR-037 / NFR-R3) — it does NOT retain the entry for display. Subscribes to
/// <see cref="IDeviceRegistry.DeviceRemoved"/> (UI-thread) to flip a device-gone banner on a
/// UUID match. Hyperlinks route through the Story 2.8 <see cref="BrowserLaunch"/> whitelist +
/// shell-execute path (NOT HyperlinkButton.NavigateUri). <see cref="IDisposable"/> unsubscribes
/// on window close — without it the singleton registry pins every Properties VM ever opened.
/// <para>This is the automated-test heart of Story 2.9; the windowing/XAML layer is App-only.</para>
/// </summary>
public sealed partial class PropertiesViewModel : ObservableObject, IDisposable
{
    private readonly Guid _uuid;
    private readonly Uri _locationUrl;
    private readonly IDeviceRegistry _registry;
    private readonly IUriLauncher _launcher;
    private readonly IDiagnosticEmitter _diag;
    private int _disposed; // Interlocked-guarded (mirror SsdpLogViewModel / DeviceTreeViewModel)

    // ── Identity (FR-052) ──
    public string FriendlyName { get; }
    public string DeviceTypeUrn { get; }
    public string Udn { get; }
    public string Uuid { get; }
    public string PresentationUrl { get; }

    // ── Manufacturer (FR-052) ──
    public string Manufacturer { get; }
    public string ManufacturerUrl { get; }
    public string ModelName { get; }
    public string ModelNumber { get; }
    public string ModelDescription { get; }
    public string ModelUrl { get; }
    public string SerialNumber { get; }
    public string Upc { get; }

    // ── Network (FR-052) ──
    public string LocationUrl { get; }
    public string Ip { get; }
    public string Port { get; }
    public string SsdpServer { get; }
    public string CacheControlMaxAgeSeconds { get; }

    // ── Discovery history (FR-052) ──
    public string FirstSeenUtc { get; }
    public string LastSeenUtc { get; }
    public string AliveCount { get; }
    public string BootId { get; }
    public string ConfigId { get; }

    // ── Embedded devices (FR-052) — Decision 5: the model flattens embedded devices into the
    // root's Services list (FR-053) and retains NO per-embedded records, so this is ALWAYS empty.
    // The XAML section renders a muted "— (services flattened per FR-053)" placeholder. Populating
    // this would require a DeviceDescriptionParser enhancement to retain the embedded <device> tree
    // (out of scope for 2.9).
    public IReadOnlyList<PropertiesViewModel> EmbeddedDevices { get; } = Array.Empty<PropertiesViewModel>();
    public bool HasEmbeddedDevices => EmbeddedDevices.Count > 0;

    // ── Resolved absolute hyperlink Uris (null when absent / unparseable / relative-unresolvable).
    // Bound as HyperlinkButton.CommandParameter; the field shows the plain "—" TextBlock when null.
    public Uri? PresentationUri { get; }
    public Uri? ManufacturerUri { get; }
    public Uri? ModelUri { get; }
    public Uri? LocationUri { get; }

    // ── Device-gone state (AC-2.9.6). No Visibility in Core (Pattern 2) — App-side converter.
    [ObservableProperty] private bool _isDeviceGone;
    [ObservableProperty] private string _deviceGoneText = "";

    public PropertiesViewModel(
        RegistryEntry entry,
        IDeviceRegistry registry,   // subscribe to DeviceRemoved (FR-037)
        IUriLauncher launcher,      // Story 2.8 shell-open seam (hyperlinks)
        IDiagnosticEmitter diag)    // Story 2.8 whitelist Warning path
    {
        _uuid = entry.Uuid;
        _locationUrl = entry.LocationUrl;
        _registry = registry;
        _launcher = launcher;
        _diag = diag;

        var desc = entry.Description;

        // Identity
        FriendlyName = OrDash(desc?.FriendlyName);
        DeviceTypeUrn = OrDash(desc?.DeviceType);
        Udn = OrDash(desc?.Udn);
        Uuid = entry.Uuid.ToString();
        PresentationUrl = OrDash(desc?.PresentationUrl);

        // Manufacturer
        Manufacturer = OrDash(desc?.Manufacturer);
        ManufacturerUrl = OrDash(desc?.ManufacturerUrl);
        ModelName = OrDash(desc?.ModelName);
        ModelNumber = OrDash(desc?.ModelNumber);
        ModelDescription = OrDash(desc?.ModelDescription);
        ModelUrl = OrDash(desc?.ModelUrl);
        SerialNumber = OrDash(desc?.SerialNumber);
        Upc = OrDash(desc?.Upc);

        // Network
        LocationUrl = entry.LocationUrl.ToString();
        Ip = entry.LocationUrl.Host;
        Port = entry.LocationUrl.Port.ToString(CultureInfo.InvariantCulture);
        SsdpServer = OrDash(entry.Server);
        CacheControlMaxAgeSeconds =
            entry.CacheControlMaxAge?.TotalSeconds.ToString(CultureInfo.InvariantCulture) ?? "—";

        // Discovery history
        FirstSeenUtc = FormatTime(entry.FirstSeenUtc);
        LastSeenUtc = FormatTime(entry.LastSeenUtc);
        AliveCount = entry.AliveCount.ToString(CultureInfo.InvariantCulture);
        BootId = OrDash(entry.BootId);
        ConfigId = OrDash(entry.ConfigId);

        // Resolved hyperlink Uris (relative URLs resolve against LocationUrl; absolute pass through).
        PresentationUri = TryResolve(desc?.PresentationUrl);
        ManufacturerUri = TryResolve(desc?.ManufacturerUrl);
        ModelUri = TryResolve(desc?.ModelUrl);
        LocationUri = TryResolve(entry.LocationUrl.ToString());

        // FR-037: device-removal survival. Data above is already snapshotted, so removal just
        // flips the banner. DeviceRemoved fires on the UI thread (registry marshals via
        // IUiDispatcher) → set observable properties directly (no dispatcher hop).
        _registry.DeviceRemoved += OnDeviceRemoved;
    }

    // AC-2.9.5: open a resolved hyperlink Uri through the Story 2.8 whitelist + shell-execute
    // path (NOT HyperlinkButton.NavigateUri, which bypasses the whitelist + Warning).
    [RelayCommand]
    private void OpenUrl(Uri? url)
    {
        if (url is null) return;
        BrowserLaunch.OpenInDefaultBrowser(url, _launcher, _diag, _uuid);
    }

    private void OnDeviceRemoved(Guid uuid)
    {
        if (uuid != _uuid || IsDeviceGone) return; // ignore other devices; idempotent
        DeviceGoneText = $"Device left the network at {DateTime.Now:HH:mm:ss}";
        IsDeviceGone = true; // data stays visible (snapshot); banner appears (XAML binds Visibility)
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _registry.DeviceRemoved -= OnDeviceRemoved;
    }

    // Absent → muted placeholder (AC-2.9.4 "absent vs empty"). Null OR empty renders "—".
    private static string OrDash(string? s) => string.IsNullOrEmpty(s) ? "—" : s;

    private static string FormatTime(DateTime utc) =>
        utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    // Resolve a raw URL string to an absolute Uri (relative against LocationUrl). Null/empty or
    // unparseable → null (the field shows "—" instead of a hyperlink).
    private Uri? TryResolve(string? raw) =>
        string.IsNullOrEmpty(raw)
            ? null
            : Uri.TryCreate(_locationUrl, raw, out var u) ? u : null;
}
