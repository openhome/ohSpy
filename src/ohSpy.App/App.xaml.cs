using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using ohSpy.App.Composition;
using ohSpy.Core.Diagnostics;
using ohSpy.Core.Threading;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace ohSpy.App;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    /// <summary>App-wide service provider. Built once during construction.</summary>
    public static IServiceProvider Services { get; private set; } = null!;

    private Window? _window;

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

        _window = new MainWindow();
        _window.Activate();
    }
}
