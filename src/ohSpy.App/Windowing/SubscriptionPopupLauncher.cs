namespace ohSpy.App.Windowing;

using Microsoft.UI.Xaml;
using ohSpy.App.Views;
using ohSpy.Core.Devices;
using ohSpy.Core.Models;
using ohSpy.Core.ViewModels;

/// <summary>
/// Pattern-7 factory delegate for the subscription popup VM (a named delegate reads cleaner in the
/// DI block than a 2-tuple <c>Func</c>). Registered in ServiceRegistration; closes over the Core
/// services the VM needs so no <c>IServiceProvider</c> leaks to the launcher.
/// </summary>
internal delegate SubscriptionPopupViewModel SubscriptionPopupViewModelFactory(
    ServiceDescription service, RegistryEntry parentEntry);

/// <summary>
/// App-side <see cref="ISubscriptionPopupLauncher"/>: constructs the SubscriptionPopupViewModel via
/// the Pattern-7 factory, news up the SubscriptionPopupWindow, applies the canonical D10 popup-open
/// sequence (Activate THEN Adopt), then kicks off the async SUBSCRIBE flow (fire-and-forget). The
/// shell window is injected post-construction by App.OnLaunched (the MainWindow is created there,
/// not in DI). Verbatim mirror of <see cref="InvocationPopupLauncher"/>. The WindowOwnershipManager
/// is already multi-child, so concurrent popups (FR-036) need no ownership change.
/// </summary>
internal sealed class SubscriptionPopupLauncher : ISubscriptionPopupLauncher
{
    private readonly SubscriptionPopupViewModelFactory _vmFactory;
    private readonly IWindowOwnershipManager _ownership;

    /// <summary>The main window, set once in App.OnLaunched. Parent for FR-046 ownership.</summary>
    public Window? ShellWindow { get; set; }

    public SubscriptionPopupLauncher(
        SubscriptionPopupViewModelFactory vmFactory, IWindowOwnershipManager ownership)
    {
        _vmFactory = vmFactory;
        _ownership = ownership;
    }

    public void Open(ServiceDescription service, RegistryEntry parentEntry)
    {
        var vm = _vmFactory(service, parentEntry);
        var window = new SubscriptionPopupWindow(vm);
        window.Activate();                                   // (1) D10: MUST precede Adopt
        if (ShellWindow is not null)
            _ownership.Adopt(window, ShellWindow);           // (2) FR-046 ownership (AC-10.5)

        // (3) Story 4.3 subscribe seam: SUBSCRIBE + attach the handle's NOTIFY/Lapsed handlers on the
        // off-thread continuation (all observable mutations marshalled via _ui.Post). Fire-and-forget —
        // every exception is handled inside InitializeAsync (failed-subscribe → banner), and the popup
        // CTS cancels it on close (OCE swallowed).
        _ = vm.InitializeAsync();
    }
}
