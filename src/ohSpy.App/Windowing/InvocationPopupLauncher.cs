namespace ohSpy.App.Windowing;

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using ohSpy.App.Views;
using ohSpy.Core.Devices;
using ohSpy.Core.Models;
using ohSpy.Core.ViewModels;

/// <summary>
/// Pattern-7 factory delegate for the invocation popup VM (a named delegate reads cleaner in the
/// DI block than a 3-tuple <c>Func</c>). Registered in ServiceRegistration; closes over the Core
/// services the VM needs so no <c>IServiceProvider</c> leaks to the launcher.
/// </summary>
internal delegate InvocationPopupViewModel InvocationPopupViewModelFactory(
    ScpdAction action, ServiceDescription parentService, RegistryEntry parentEntry);

/// <summary>
/// App-side <see cref="IInvocationPopupLauncher"/>: constructs the InvocationPopupViewModel via the
/// Pattern-7 factory, news up the InvocationPopupWindow, and applies the canonical D10 popup-open
/// sequence (Activate THEN Adopt). The shell window is injected post-construction by App.OnLaunched
/// (the MainWindow is created there, not in DI). Verbatim mirror of <see cref="PropertiesLauncher"/>.
/// </summary>
internal sealed class InvocationPopupLauncher : IInvocationPopupLauncher
{
    private readonly InvocationPopupViewModelFactory _vmFactory;
    private readonly IWindowOwnershipManager _ownership;

    /// <summary>The main window, set once in App.OnLaunched. Parent for FR-046 ownership.</summary>
    public Window? ShellWindow { get; set; }

    public InvocationPopupLauncher(
        InvocationPopupViewModelFactory vmFactory, IWindowOwnershipManager ownership)
    {
        _vmFactory = vmFactory;
        _ownership = ownership;
    }

    public void Open(ScpdAction action, ServiceDescription parentService, RegistryEntry parentEntry)
    {
        var vm = _vmFactory(action, parentService, parentEntry);
        var window = new InvocationPopupWindow(vm);
        window.Activate();                                   // (1) D10: MUST precede Adopt
        if (ShellWindow is not null)
            _ownership.Adopt(window, ShellWindow);           // (2) FR-046 ownership (AC-10.5)

        // (2a) This popup is opened by DOUBLE-CLICKING an action row. The second click's mouse-up
        // re-focuses the shell AFTER this synchronous Activate(), and post-A31 (popups float in free
        // z-order, no owner link) that drops the just-opened popup behind the shell. Re-assert it on
        // top at Low priority — which runs once the double-click input has fully unwound — so the
        // popup ends up in front. (One-shot; it then floats freely per A31 — clicking the shell still
        // brings it forward.) The other three popups open from single menu clicks and don't hit this.
        window.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, window.Activate);

        // (3) Story 3.3 async-init seam: fetch+parse the SCPD state table and upgrade the ctor's
        // text-only inputs into constrained variants (AC-3.3.1). Fire-and-forget — every exception
        // is handled inside InitializeAsync, and the popup CTS cancels it on close (OCE swallowed).
        _ = vm.InitializeAsync();
    }
}
