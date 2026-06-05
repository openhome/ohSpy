namespace ohSpy.App.Windowing;

using Microsoft.UI.Xaml;
using ohSpy.App.Views;
using ohSpy.Core.ViewModels;

/// <summary>
/// App-side <see cref="IDiagnosticsLauncher"/> (Story 5.1, FR-041 / FR-046). Owns the SINGLE
/// app-lifetime Diagnostics viewer: <see cref="Open"/> news up the <see cref="DiagnosticsWindow"/> on
/// first call (binding the singleton <see cref="DiagnosticsViewModel"/>), applies the canonical D10
/// popup-open sequence (Activate THEN Adopt — AC-10.5; A31 free z-order), and tracks the live window so
/// a second <see cref="Open"/> simply re-activates it instead of creating another (FR-041 "the
/// Diagnostics viewer", singular). The shell window is injected post-construction by App.OnLaunched
/// (the MainWindow is created there, not in DI). Verbatim mirror of <see cref="SubscriptionPopupLauncher"/>,
/// minus the per-open VM factory (one singleton VM) and the async init kick-off (the VM is passive —
/// it binds the live ring).
/// </summary>
internal sealed class DiagnosticsLauncher : IDiagnosticsLauncher
{
    private readonly DiagnosticsViewModel _viewModel;
    private readonly IWindowOwnershipManager _ownership;

    private DiagnosticsWindow? _window;

    /// <summary>The main window, set once in App.OnLaunched. Parent for FR-046 ownership.</summary>
    public Window? ShellWindow { get; set; }

    public DiagnosticsLauncher(DiagnosticsViewModel viewModel, IWindowOwnershipManager ownership)
    {
        _viewModel = viewModel;
        _ownership = ownership;
    }

    public void Open()
    {
        // Single app-lifetime viewer: if one is already open, just bring it forward.
        if (_window is not null)
        {
            _window.Activate();
            return;
        }

        var window = new DiagnosticsWindow(_viewModel);
        // Forget the handle when the viewer is closed so a later Open re-creates it cleanly.
        window.Closed += (_, _) => _window = null;
        _window = window;

        window.Activate();                                   // (1) D10: MUST precede Adopt
        if (ShellWindow is not null)
            _ownership.Adopt(window, ShellWindow);           // (2) FR-046 ownership (AC-10.5)
    }
}
