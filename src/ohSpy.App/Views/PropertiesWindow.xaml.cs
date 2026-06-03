namespace ohSpy.App.Views;

using System.ComponentModel;
using Microsoft.UI.Xaml;
using ohSpy.Core.ViewModels;

/// <summary>
/// Read-only Properties popup (FR-052). Pattern 13: constructor-only code-behind — the only
/// logic is the <see cref="Window.Closed"/> handler (disposes the VM so it unsubscribes from
/// <see cref="ohSpy.Core.Devices.IDeviceRegistry.DeviceRemoved"/>) plus a handful of App-side
/// <see cref="Visibility"/> projections. The VM exposes <c>bool</c>/<c>Uri?</c> (Pattern 2 forbids
/// <see cref="Visibility"/> in Core); mapping to Visibility belongs here. These are exposed as
/// code-behind properties rather than <c>x:Bind</c> converters because the binding root is a
/// <see cref="Window"/> (not a FrameworkElement), so the XAML converter-lookup-root is unavailable.
/// </summary>
public sealed partial class PropertiesWindow : Window, INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    // Exposed as a typed property so x:Bind in XAML can reference it at compile time (MainWindow precedent).
    public PropertiesViewModel ViewModel { get; }

    public PropertiesWindow(PropertiesViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        Title = "Device Properties";

        // The device-gone banner visibility tracks the VM's observable IsDeviceGone.
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;

        Closed += OnClosed; // sync void (VSTHRD100); dispose the VM to unsubscribe from the registry
    }

    // ── Device-gone banner (AC-2.9.6) — runtime-notifying. ──
    public Visibility BannerVisibility => ToVisibility(ViewModel.IsDeviceGone);

    // ── Per-field hyperlink-vs-text projections (AC-2.9.5) — static (set at VM construction). ──
    public Visibility PresentationLinkVisibility => ToVisibility(ViewModel.PresentationUri is not null);
    public Visibility PresentationTextVisibility => ToVisibility(ViewModel.PresentationUri is null);
    public Visibility ManufacturerLinkVisibility => ToVisibility(ViewModel.ManufacturerUri is not null);
    public Visibility ManufacturerTextVisibility => ToVisibility(ViewModel.ManufacturerUri is null);
    public Visibility ModelLinkVisibility => ToVisibility(ViewModel.ModelUri is not null);
    public Visibility ModelTextVisibility => ToVisibility(ViewModel.ModelUri is null);
    public Visibility LocationLinkVisibility => ToVisibility(ViewModel.LocationUri is not null);
    public Visibility LocationTextVisibility => ToVisibility(ViewModel.LocationUri is null);

    // ── Embedded-devices placeholder (Decision 5) — always visible while the list is empty. ──
    public Visibility EmbeddedPlaceholderVisibility => ToVisibility(!ViewModel.HasEmbeddedDevices);

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PropertiesViewModel.IsDeviceGone))
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BannerVisibility)));
    }

    private static Visibility ToVisibility(bool flag) => flag ? Visibility.Visible : Visibility.Collapsed;

    // MUST be synchronous void (the Window.Closed delegate returns void; async void is
    // App-tree-fatal per VSTHRD100). Dispose() is synchronous — fine.
    private void OnClosed(object sender, WindowEventArgs args)
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ViewModel.Dispose();
    }
}
