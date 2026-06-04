namespace ohSpy.Core.ViewModels;

using ohSpy.Core.Devices;
using ohSpy.Core.Models;

/// <summary>
/// Core seam for opening the (App-layer) subscription popup for a device service (Story 4.3,
/// FR-032). Implemented in ohSpy.App (SubscriptionPopupLauncher) because constructing a WinUI
/// Window is not a Core concern (Pattern 2 / CoreAppBoundaryTests forbids Core → App). Lets
/// ServiceNodeViewModel.SubscribeCommand (Core) trigger the popup across the Core/App boundary —
/// the verbatim 3.2 <see cref="IInvocationPopupLauncher"/> precedent. The App impl applies the
/// canonical Decision 10 sequence: <c>window.Activate()</c> THEN
/// <c>WindowOwnershipManager.Adopt(window, ShellWindow)</c>, then kicks off the subscribe flow.
/// </summary>
public interface ISubscriptionPopupLauncher
{
    /// <summary>
    /// Open the subscription popup for <paramref name="service"/> of device <paramref name="parentEntry"/>
    /// (UI-thread; fire-and-forget). The launcher resolves the popup ViewModel via a Pattern-7 factory
    /// so no <c>IServiceProvider</c> leaks to the call site. Multiple concurrent popups across different
    /// services run independently (FR-036) — each owns its own handle, CTS, and collections.
    /// </summary>
    void Open(ServiceDescription service, RegistryEntry parentEntry);
}
