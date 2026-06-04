namespace ohSpy.App.Views;

using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using ohSpy.Core.ViewModels;

/// <summary>
/// Subscription popup window (Story 4.3, FR-032). Pattern 13: constructor-only code-behind — the
/// only logic is the <see cref="Window.Closed"/> handler (disposes the VM so it cancels the
/// subscribe/renew, UNSUBSCRIBEs the handle, and unsubscribes from
/// <see cref="ohSpy.Core.Devices.IDeviceRegistry.DeviceRemoved"/>) plus the App-side
/// <see cref="SubscriptionStatus"/>/<c>bool</c> → <see cref="Visibility"/> projections the VM cannot
/// carry (Pattern 2 forbids <see cref="Visibility"/> in Core; mirror <c>InvocationPopupWindow.xaml.cs</c>).
/// These are code-behind properties rather than XAML converters because the binding root is a
/// <see cref="Window"/> (not a FrameworkElement), so the converter-lookup-root is unavailable.
/// </summary>
public sealed partial class SubscriptionPopupWindow : Window, INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    // Exposed as a typed property so x:Bind in XAML can reference it at compile time (MainWindow precedent).
    public SubscriptionPopupViewModel ViewModel { get; }

    public SubscriptionPopupWindow(SubscriptionPopupViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        Title = "Subscribe: " + viewModel.Title;

        // Re-project Visibility / text whenever the VM's observable state changes.
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        // The "Latest property values" empty-hint flips on the first merged row.
        ViewModel.LatestPropertyValues.CollectionChanged += OnLatestChanged;

        Closed += OnClosed; // sync void (VSTHRD100); dispose the VM to cancel + UNSUBSCRIBE + unsubscribe
    }

    // ── Status-driven banner (AC-4.3.4 / .5). Visible for every non-active status. ──
    public Visibility BannerVisibility =>
        ToVisibility(ViewModel.Status is SubscriptionStatus.Lapsed
            or SubscriptionStatus.DeviceGone
            or SubscriptionStatus.FailedToSubscribe);

    public string BannerText => ViewModel.StatusMessage ?? "";

    // ── A small live status line in the header. ──
    public string StatusLine => ViewModel.Status switch
    {
        SubscriptionStatus.Subscribing => "Subscribing…",
        SubscriptionStatus.Subscribed => ViewModel.StatusMessage is { Length: > 0 } m ? $"Subscribed · {m}" : "Subscribed",
        SubscriptionStatus.Lapsed => "Lapsed",
        SubscriptionStatus.DeviceGone => "Device gone",
        SubscriptionStatus.FailedToSubscribe => "Failed to subscribe",
        _ => "",
    };

    // ── "Latest property values" empty hint. ──
    public Visibility NoLatestVisibility => ToVisibility(ViewModel.LatestPropertyValues.Count == 0);

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SubscriptionPopupViewModel.Status):
                Raise(nameof(BannerVisibility));
                Raise(nameof(StatusLine));
                break;
            case nameof(SubscriptionPopupViewModel.StatusMessage):
                Raise(nameof(BannerText));
                Raise(nameof(StatusLine));
                break;
        }
    }

    private void OnLatestChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        Raise(nameof(NoLatestVisibility));

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static Visibility ToVisibility(bool flag) => flag ? Visibility.Visible : Visibility.Collapsed;

    // MUST be synchronous void (the Window.Closed delegate returns void; async void is App-tree-fatal
    // per VSTHRD100). Dispose() is synchronous — it cancels + fire-and-forget UNSUBSCRIBEs the handle
    // and unsubscribes from the registry (AC-4.3.9).
    private void OnClosed(object sender, WindowEventArgs args)
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ViewModel.LatestPropertyValues.CollectionChanged -= OnLatestChanged;
        ViewModel.Dispose();
    }
}
