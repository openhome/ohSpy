namespace ohSpy.Core.ViewModels;

using ohSpy.Core.Devices;
using ohSpy.Core.Models;

/// <summary>
/// Core seam for opening the (App-layer) invocation popup for a device action (Story 3.2,
/// FR-025). Implemented in ohSpy.App (InvocationPopupLauncher) because constructing a WinUI
/// Window is not a Core concern (Pattern 2 / CoreAppBoundaryTests forbids Core → App). Lets
/// ActionNodeViewModel.OpenInvocationPopupCommand (Core) trigger the popup across the Core/App
/// boundary — the verbatim 2.9 <see cref="IPropertiesLauncher"/> precedent. The App impl
/// applies the canonical Decision 10 sequence: <c>window.Activate()</c> THEN
/// <c>WindowOwnershipManager.Adopt(window, ShellWindow)</c>.
/// </summary>
public interface IInvocationPopupLauncher
{
    /// <summary>
    /// Open the invocation popup for <paramref name="action"/> on <paramref name="parentService"/>
    /// of device <paramref name="parentEntry"/> (UI-thread; fire-and-forget). The launcher resolves
    /// the popup ViewModel via a Pattern-7 factory so no <c>IServiceProvider</c> leaks to the call site.
    /// </summary>
    void Open(ScpdAction action, ServiceDescription parentService, RegistryEntry parentEntry);
}
