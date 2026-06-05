namespace ohSpy.Core.ViewModels;

/// <summary>
/// Core seam for opening the (App-layer) Diagnostics viewer window (Story 5.1, FR-041 / FR-046).
/// Implemented in ohSpy.App (<c>DiagnosticsLauncher</c>) because constructing a WinUI Window is not a
/// Core concern (Pattern 2 / CoreAppBoundaryTests forbids Core → App). Lets
/// <see cref="ShellViewModel.OpenDiagnosticsCommand"/> (Core) trigger the viewer across the Core/App
/// boundary — the verbatim 2.9/3.2/4.3 launcher-seam precedent (<see cref="ISubscriptionPopupLauncher"/>).
/// The App impl applies the canonical Decision 10 sequence: <c>window.Activate()</c> THEN
/// <c>WindowOwnershipManager.Adopt(window, ShellWindow)</c> (AC-10.5; A31 free z-order).
/// </summary>
public interface IDiagnosticsLauncher
{
    /// <summary>
    /// Open the Diagnostics viewer (UI-thread). There is a SINGLE app-lifetime viewer (FR-041/FR-046
    /// say "the Diagnostics viewer", singular): a second <see cref="Open"/> re-activates the existing
    /// window rather than creating another. No arguments — the viewer binds the singleton ring sink.
    /// </summary>
    void Open();
}
