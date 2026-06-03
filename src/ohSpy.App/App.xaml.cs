using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using ohSpy.App.Composition;
using ohSpy.App.Windowing;
using ohSpy.Core.Devices;
using ohSpy.Core.Diagnostics;
using ohSpy.Core.Threading;
using ohSpy.Core.ViewModels;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace ohSpy.App;

// CA1001: App owns the app-lifetime disposables _appCts (CancellationTokenSource) and
// _shellVm (IAsyncDisposable) per Decision 7. WinUI's Application base exposes no
// IDisposable contract for the framework to invoke, and _shellVm is async-disposable
// (a synchronous Dispose would violate Pattern 6's no-blocking-on-async rule). Deterministic
// teardown happens in OnWindowClosed → ShutdownAsync instead.
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Microsoft.Design", "CA1001:TypesThatOwnDisposableFieldsShouldBeDisposable",
    Justification = "WinUI Application has no IDisposable contract; teardown is in OnWindowClosed.")]
public partial class App : Application
{
    /// <summary>App-wide service provider. Built once during construction.</summary>
    public static IServiceProvider Services { get; private set; } = null!;

    private Window? _window;

    /// <summary>
    /// App-level cancellation source — the top of the Decision 7 hierarchy
    /// (<c>app → adapter → device → popup</c>). Cancelled on window close.
    /// </summary>
    private readonly CancellationTokenSource _appCts = new();

    /// <summary>
    /// The shell view model (Story 2.5). Owns the AdapterScope lifetime (Amendment A26).
    /// Null until <see cref="OnLaunched"/>.
    /// </summary>
    private ShellViewModel? _shellVm;

    /// <summary>
    /// Initializes the singleton application object.  This is the first line of authored code
    /// executed, and as such is the logical equivalent of main() or WinMain().
    /// </summary>
    public App()
    {
        InitializeComponent();

        // Compose DI graph. WinUiDispatcher's ctor is deferred until first
        // GetRequiredService<IUiDispatcher>() call in OnLaunched (UI thread).
        Services = new ServiceCollection()
            .RegisterServices()
            .BuildServiceProvider();
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        // Force IUiDispatcher construction on the UI thread so WinUiDispatcher
        // captures DispatcherQueue.GetForCurrentThread() correctly. Singleton —
        // subsequent resolves return this same instance.
        _ = Services.GetRequiredService<IUiDispatcher>();

        // Story 1.5: late-bind the ring sink into the file sink so the AC-8.6
        // startup-failure warning path can emit through the ring. The two singletons
        // can't reference each other via constructor injection (circular dep); the
        // App's composition root resolves both and wires them post-construction. The
        // IUiDispatcher pin above MUST remain first — resolving IDiagnosticRingSink
        // transitively resolves IUiDispatcher (ring sink ctor depends on it), but
        // keeping the explicit pin preserves the documented "force UI-thread capture
        // before any other DI resolve" intent and gives a clear failure mode if a
        // future refactor breaks the order.
        Services.GetRequiredService<DiagnosticFileSink>().SetRingSink(
            Services.GetRequiredService<IDiagnosticRingSink>());

        // Story 2.3: construct the eager-description dispatcher so it subscribes to the
        // registry's fetch-trigger before any SSDP alive is processed (DiscoveryService
        // wiring lands in 2.4). Also validates the full DI graph resolves with no cycle.
        _ = Services.GetRequiredService<EagerDescriptionDispatcher>();

        // Story 2.5: resolve ShellViewModel (owns AdapterScope — Amendment A26 migration).
        // Fire-and-forget StartAsync; exceptions handled inside ShellViewModel.RunStartAsync.
        _shellVm = Services.GetRequiredService<ShellViewModel>();
        _ = _shellVm.StartAsync(_appCts.Token);

        _window = new MainWindow(_shellVm);
        // Story 2.9: give the Properties-popup launcher its FR-046 parent (the MainWindow is
        // created here, not in DI, so the shell window is injected post-construction).
        Services.GetRequiredService<PropertiesLauncher>().ShellWindow = _window;
        // Story 3.2: same FR-046 parent injection for the invocation-popup launcher.
        Services.GetRequiredService<InvocationPopupLauncher>().ShellWindow = _window;
        _window.Closed += OnWindowClosed;
        _window.Activate();
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _ = ShutdownAsync();
    }

    private async Task ShutdownAsync()
    {
        // Decision 7: cancel the app token first so all linked child scopes
        // (adapter → device → popup) receive the signal before teardown begins.
        // The adapter scope's own DisposeAsync also cancels its linked CTS, but
        // signalling the parent first ensures future components holding _appCts.Token
        // directly (DiscoveryService, GENA) observe cancellation promptly.
        await _appCts.CancelAsync();

        if (_shellVm is not null)
            await _shellVm.DisposeAsync().ConfigureAwait(false);

        _appCts.Dispose();
    }
}
