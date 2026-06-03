namespace ohSpy.App.Composition;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ohSpy.App.Windowing;
using ohSpy.Core.Devices;
using ohSpy.Core.Diagnostics;
using ohSpy.Core.Discovery;
using ohSpy.Core.Http;
using ohSpy.Core.Scpd;
using ohSpy.Core.Threading;
using ohSpy.Core.ViewModels;

/// <summary>
/// Single composition root for the App. Future stories add their service registrations
/// here. Pattern 7 — singleton default, no per-request scopes.
/// </summary>
internal static class ServiceRegistration
{
    public static IServiceCollection RegisterServices(this IServiceCollection services)
    {
        // Story 1.2 — IUiDispatcher (Decision 1). Must be resolved on the UI thread
        // for its first instantiation so WinUiDispatcher captures DispatcherQueue
        // correctly. See App.OnLaunched for the resolve-and-pin call.
        services.AddSingleton<IUiDispatcher, WinUiDispatcher>();

        // Story 1.3 — HTTP client facade (Decision 3) + timeout options (Decision 11).
        // Singleton lifetime: UpnpHttpClient owns a single shared HttpClient over a
        // SocketsHttpHandler with PooledConnectionLifetime=2min for DNS-refresh resilience.
        // Do NOT change to AddTransient — that would create a new handler+client per resolve
        // and exhaust sockets under SSDP burst.
        services.Configure<HttpTimeoutOptions>(_ => { /* defaults from HttpTimeoutOptions ctor */ });
        services.AddSingleton<IUpnpHttpClient, UpnpHttpClient>();

        // Story 1.5 — full diagnostic pipeline. REPLACES Story 1.3's NoOpDiagnosticEmitter
        // placeholder. Required ordering: identity lookup + ring sink + file sink BEFORE
        // emitter (emitter constructor depends on all three). Stories that consume
        // IDiagnosticEmitter (1.3 HTTP facade, 1.4 parsers' callers) get the real one
        // transparently — no call-site code changes.
        services.Configure<DiagnosticOptions>(_ => { /* MinSeverity defaults to Information */ });

        // Story 2.3: registry-backed identity resolution replaces the NullIdentityLookup
        // placeholder. Resolves device UUID → friendly name for the FR-041 Identity column.
        services.AddSingleton<IDiagnosticIdentityLookup, RegistryIdentityLookup>();

        // Ring sink (Core): bounded observable collection + UI-dispatcher-posted prepend.
        services.AddSingleton<IDiagnosticRingSink, DiagnosticRingSink>();

        // File sink (App): channel + background pump + rotation + late-bound ring sink for
        // AC-8.6 startup-failure warning emission. Concrete-type registration exposed so
        // App.OnLaunched can call SetRingSink after the provider builds.
        services.AddSingleton<DiagnosticFileSink>();
        services.AddSingleton<IDiagnosticFileSink>(sp => sp.GetRequiredService<DiagnosticFileSink>());

        // Emitter: fan-out to MEL ILogger + ring sink + file sink. Replaces NoOp.
        services.AddSingleton<IDiagnosticEmitter, DiagnosticEmitter>();

        // MEL ILogger plumbing — without this, the constructor's ILogger<DiagnosticEmitter>
        // dependency won't resolve. AddLogging() registers ILoggerFactory + ILogger<T>. No
        // additional providers configured (DiagnosticEmitter is the consumer; MEL is a
        // pass-through to dotnet-trace).
        services.AddLogging();

        // Story 1.4 — XML parsers (Decision 5). Stateless across documents; singleton fine.
        services.AddSingleton<IScpdParser, XmlReaderScpdParser>();
        services.AddSingleton<IDeviceDescriptionParser, DeviceDescriptionParser>();

        // Story 2.1 — SSDP transport (Decision 2). Singleton: the type is resolvable, but
        // its lifecycle (StartAsync / DisposeAsync per adapter) is owned by AdapterScope
        // (Story 2.2). No consumer wires it until DiscoveryService (Story 2.4).
        services.AddSingleton<ISsdpTransport, SsdpTransport>();

        // Story 2.2 — Network adapter enumeration (FR-048). Singletons: stateless query
        // services. AdapterScope is NOT registered here — it is constructed by the
        // app-startup orchestrator (App.OnLaunched; relocated to ShellViewModel in 2.5)
        // because its lifetime is bounded by adapter selection (Pattern 7 + Decision 7).
        services.AddSingleton<INetworkInterfaceSource, LiveNetworkInterfaceSource>();
        services.AddSingleton<INetworkAdapterEnumerator, NetworkAdapterEnumerator>();

        // Story 2.3 — Device registry (Decision 9). Concrete + interface forward to ONE
        // singleton so EagerDescriptionDispatcher can reach the internal
        // Remove/RaiseDeviceLoaded/EntryNeedsFetch surface (DiagnosticFileSink precedent).
        services.AddSingleton<DeviceRegistry>();
        services.AddSingleton<IDeviceRegistry>(sp => sp.GetRequiredService<DeviceRegistry>());

        // Eager description dispatcher (Decision 9 + Decision 3). Subscribes to the registry's
        // EntryNeedsFetch in its ctor — pinned at startup in App.OnLaunched to wire the
        // subscription. No consumer feeds the registry until DiscoveryService (Story 2.4).
        services.AddSingleton<EagerDescriptionDispatcher>();

        // Story 2.4 — SSDP parser + discovery service.
        // SsdpParser is internal; registered as concrete so DiscoveryService can receive it.
        services.AddSingleton<SsdpParser>();
        services.AddSingleton<DiscoveryService>();
        services.AddSingleton<IDiscoveryService>(sp => sp.GetRequiredService<DiscoveryService>());

        // Story 2.6 — NodeServices bundle: the Core services the tree-node VMs need to lazily
        // fetch + parse an SCPD on expand. All four members are already-registered singletons;
        // the bundle itself is a stateless singleton threaded into the VM graph by ShellViewModel.
        services.AddSingleton<NodeServices>();

        // Story 2.5 — Main window shell ViewModel. Singleton: one window, one ShellViewModel.
        // ShellViewModel owns the AdapterScope lifetime (Amendment A26 migration from App.xaml.cs).
        // DeviceTreeViewModel is constructed by ShellViewModel, not registered separately.
        services.AddSingleton<ShellViewModel>();

        return services;
    }
}
