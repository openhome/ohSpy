namespace ohSpy.App.Views;

using System.ComponentModel;
using Microsoft.UI.Xaml;
using ohSpy.Core.Models;
using ohSpy.Core.ViewModels;

/// <summary>
/// Invocation popup window (Story 3.2, FR-025). Pattern 13: constructor-only code-behind — the
/// only logic is the <see cref="Window.Closed"/> handler (disposes the VM so it cancels the
/// in-flight invocation + unsubscribes from <see cref="ohSpy.Core.Devices.IDeviceRegistry.DeviceRemoved"/>)
/// plus the App-side <c>bool</c>/result-type → <see cref="Visibility"/> projections the VM cannot
/// carry (Pattern 2 forbids <see cref="Visibility"/> in Core; mirror <c>PropertiesWindow.xaml.cs</c>).
/// These are code-behind properties rather than XAML converters because the binding root is a
/// <see cref="Window"/> (not a FrameworkElement), so the converter-lookup-root is unavailable.
/// </summary>
public sealed partial class InvocationPopupWindow : Window, INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    // Exposed as a typed property so x:Bind in XAML can reference it at compile time (MainWindow precedent).
    public InvocationPopupViewModel ViewModel { get; }

    public InvocationPopupWindow(InvocationPopupViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        Title = "Invoke: " + viewModel.Title;

        // Re-project Visibility whenever the VM's observable state changes.
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;

        Closed += OnClosed; // sync void (VSTHRD100); dispose the VM to cancel + unsubscribe
    }

    // ── Static (set at VM construction). ──
    public Visibility NoInputsVisibility => ToVisibility(ViewModel.Inputs.Count == 0);

    // ── Runtime-notifying (IsInvoking). ──
    public Visibility InvokingVisibility => ToVisibility(ViewModel.IsInvoking);
    // AC-3.2.6 #19 / NFR-UI3: input TextBoxes disabled while a call is in flight. Bound at
    // the ItemsControl level so the disable propagates to all child controls (WinUI IsEnabled
    // inheritance). Pattern 2: IsEnabled stays out of Core.
    public bool IsInputEnabled => !ViewModel.IsInvoking;

    // ── Runtime-notifying (IsDeviceGone). ──
    public Visibility BannerVisibility => ToVisibility(ViewModel.IsDeviceGone);

    // ── Runtime-notifying (Result variant). ──
    public Visibility NoResultVisibility => ToVisibility(ViewModel.Result is null);
    public Visibility SuccessVisibility => ToVisibility(ViewModel.Result is SuccessResult);
    public Visibility FaultVisibility => ToVisibility(ViewModel.Result is FaultResult);
    public Visibility TransportVisibility => ToVisibility(ViewModel.Result is TransportErrorResult);

    public IReadOnlyList<SoapArgument> SuccessOutputs =>
        ViewModel.Result is SuccessResult s ? s.Outputs : System.Array.Empty<SoapArgument>();

    public Visibility SuccessNoOutputVisibility =>
        ToVisibility(ViewModel.Result is SuccessResult { Outputs.Count: 0 });

    public string FaultText =>
        ViewModel.Result is FaultResult f
            ? $"HTTP {f.StatusCode} · error {f.ErrorCode}: {f.ErrorDescription}"
            : "";

    public string TransportText =>
        ViewModel.Result is TransportErrorResult t ? t.Message : "";

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(InvocationPopupViewModel.IsInvoking):
                Raise(nameof(InvokingVisibility));
                Raise(nameof(IsInputEnabled));
                break;
            case nameof(InvocationPopupViewModel.IsDeviceGone):
                Raise(nameof(BannerVisibility));
                break;
            case nameof(InvocationPopupViewModel.Result):
                Raise(nameof(NoResultVisibility));
                Raise(nameof(SuccessVisibility));
                Raise(nameof(FaultVisibility));
                Raise(nameof(TransportVisibility));
                Raise(nameof(SuccessOutputs));
                Raise(nameof(SuccessNoOutputVisibility));
                Raise(nameof(FaultText));
                Raise(nameof(TransportText));
                break;
        }
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static Visibility ToVisibility(bool flag) => flag ? Visibility.Visible : Visibility.Collapsed;

    // MUST be synchronous void (the Window.Closed delegate returns void; async void is
    // App-tree-fatal per VSTHRD100). Dispose() is synchronous — it cancels the in-flight
    // invocation (OCE swallowed in InvokeAsync) and unsubscribes from the registry (AC-3.2.10).
    private void OnClosed(object sender, WindowEventArgs args)
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ViewModel.Dispose();
    }
}
