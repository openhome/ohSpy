namespace ohSpy.App.Windowing;

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

        // (3) Story 3.3 async-init seam: fetch+parse the SCPD state table and upgrade the ctor's
        // text-only inputs into constrained variants (AC-3.3.1). Fire-and-forget — every exception
        // is handled inside InitializeAsync, and the popup CTS cancels it on close (OCE swallowed).
        _ = vm.InitializeAsync();
    }
}
