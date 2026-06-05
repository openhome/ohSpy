namespace ohSpy.App.Composition;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ohSpy.App.Windowing;
using ohSpy.Core.Devices;
using ohSpy.Core.Diagnostics;
using ohSpy.Core.Discovery;
using ohSpy.Core.Events;
using ohSpy.Core.Http;
using ohSpy.Core.Scpd;
using ohSpy.Core.Shell;
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

        // Story 5.1 (Q1) — runtime-mutable emitter-severity gate. The DiagnosticEmitter reads THIS
        // (a single Volatile.Read of an int) on every emit instead of the init-only
        // DiagnosticOptions.MinSeverity, so the Diagnostics viewer's MinSeverity control can flip the
        // Verbose firehose on/off at runtime. Seeded FROM DiagnosticOptions.MinSeverity at construction
        // so the configured startup default is preserved. Singleton (one gate, shared by the emitter and
        // the viewer VM). MUST be registered BEFORE the emitter (ctor dependency).
        services.AddSingleton<IDiagnosticLevelGate, DiagnosticLevelGate>();

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

        // Story 2.1 — SSDP transport (Decision 2). Amendment A23 (Story 5.2): registered as a
        // Func<ISsdpTransport> FACTORY, not a singleton — the atomic adapter switch must dispose the
        // old transport and construct a fresh one bound to the new adapter (a disposed singleton can
        // never re-bind: StartAsync's double-start guard + sockets/fields are not reset). AdapterScope
        // OWNS the transport it constructs via this factory and exposes its IncomingDatagrams reader so
        // DiscoveryService reads the scope-owned instance. Func<> (not a bespoke ISsdpTransportFactory)
        // matches the project's existing Pattern-7 Func<> factories (the 2.9/3.2/4.3 popup-VM factories).
        services.AddSingleton<Func<ISsdpTransport>>(sp =>
            () => new SsdpTransport(sp.GetRequiredService<IDiagnosticEmitter>()));

        // Story 4.1 — GENA inbound callback host (Decision 4). The FIRST inbound listener: a raw
        // TcpListener bound to the selected adapter IP (NOT 0.0.0.0), so it runs unelevated with no
        // URL ACL (FR-049). Amendment A23 (Story 5.2): registered as a Func<IEventCallbackHost> FACTORY
        // (the ISsdpTransport precedent) — like the transport, the host CANNOT re-start after dispose
        // (StartAsync double-start guard + _listener/_slots/_runCts/_callbackBaseUrl never reset), so
        // the atomic switch disposes the old host and constructs a fresh one. ShellViewModel owns the
        // LIVE host instance (constructs it on startup + on each switch), starts it once
        // scope.CurrentAdapterIPv4 is known, hands it to ISubscriptionClient.SetCallbackHost, and drains
        // it in DisposeAsync / before each rebuild.
        services.AddSingleton<Func<IEventCallbackHost>>(sp =>
            () => new EventCallbackHost(
                sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<HttpTimeoutOptions>>(),
                sp.GetRequiredService<IDiagnosticEmitter>()));

        // Story 4.2 — GENA subscription lifecycle orchestrator (the first consumer of the 4.1 host +
        // the 1.3 GENA verbs). Singleton. A23 (Story 5.2): the callback host is NO LONGER ctor-injected
        // (the host is now a per-adapter factory, disposed+rebuilt on switch) — ShellViewModel hands the
        // LIVE host to SetCallbackHost on startup AND each switch, so the client re-subscribes
        // NotifyReceived + re-points CallbackBaseUrl to the new host. ShellViewModel.RunStartAsync also
        // calls SetAdapterContext(scope.AdapterToken) (the level-above token for the D7
        // UNSUBSCRIBE-on-close + the adapter-switch lapse cascade); the switch re-SetAdapterContext on the
        // new adapter (the old adapter-token cancel already cascades into every renew loop → AdapterSwitch lapse).
        services.AddSingleton<ISubscriptionClient, SubscriptionClient>();

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

        // Story 2.8 — OS shell-open seam for the context-menu "Fetch XML" commands (FR-019/020).
        services.AddSingleton<IUriLauncher, ShellUriLauncher>();

        // Story 2.9 — window ownership (D10) + Properties popup (FR-052). Registered BEFORE the
        // NodeServices line so IPropertiesLauncher auto-resolves into the bundle.
        services.AddSingleton<IWindowOwnershipManager, WindowOwnershipManager>();
        // Pattern 7: per-popup VM factory — no IServiceProvider leak at the call site.
        services.AddSingleton<Func<RegistryEntry, PropertiesViewModel>>(sp =>
            entry => new PropertiesViewModel(
                entry,
                sp.GetRequiredService<IDeviceRegistry>(),
                sp.GetRequiredService<IUriLauncher>(),
                sp.GetRequiredService<IDiagnosticEmitter>()));
        // Concrete + interface (dual reg, DiscoveryService precedent) so OnLaunched can set ShellWindow.
        services.AddSingleton<PropertiesLauncher>();
        services.AddSingleton<IPropertiesLauncher>(sp => sp.GetRequiredService<PropertiesLauncher>());

        // Story 3.2 — invocation popup (FR-025). Same shape as the 2.9 Properties launcher block.
        // Registered BEFORE the NodeServices line so IInvocationPopupLauncher auto-resolves into the
        // bundle. Pattern 7: a named factory delegate (no IServiceProvider leak at the call site).
        services.AddSingleton<InvocationPopupViewModelFactory>(sp =>
            (action, parentService, parentEntry) => new InvocationPopupViewModel(
                action,
                parentService,
                parentEntry,
                sp.GetRequiredService<IUpnpHttpClient>(),
                sp.GetRequiredService<IUiDispatcher>(),
                sp.GetRequiredService<IDiagnosticEmitter>(),
                sp.GetRequiredService<IDeviceRegistry>(),
                sp.GetRequiredService<IScpdParser>())); // Story 3.3: state-table fetch for constrained inputs
        // Concrete + interface (dual reg) so OnLaunched can set ShellWindow.
        services.AddSingleton<InvocationPopupLauncher>();
        services.AddSingleton<IInvocationPopupLauncher>(sp => sp.GetRequiredService<InvocationPopupLauncher>());

        // Story 4.3 — subscription popup (FR-032). Same shape as the 3.2 invocation launcher block.
        // Registered BEFORE the NodeServices line so ISubscriptionPopupLauncher auto-resolves into the
        // bundle. Pattern 7: a named factory delegate (no IServiceProvider leak at the call site). The VM
        // is the first/only consumer of the singleton ISubscriptionClient (4.2 seam).
        services.AddSingleton<SubscriptionPopupViewModelFactory>(sp =>
            (service, parentEntry) => new SubscriptionPopupViewModel(
                service,
                parentEntry,
                sp.GetRequiredService<ISubscriptionClient>(),
                sp.GetRequiredService<IUiDispatcher>(),
                sp.GetRequiredService<IDiagnosticEmitter>(),
                sp.GetRequiredService<IDeviceRegistry>()));
        // Concrete + interface (dual reg) so OnLaunched can set ShellWindow.
        services.AddSingleton<SubscriptionPopupLauncher>();
        services.AddSingleton<ISubscriptionPopupLauncher>(sp => sp.GetRequiredService<SubscriptionPopupLauncher>());

        // Story 2.6 — NodeServices bundle: the Core services the tree-node VMs need to lazily
        // fetch + parse an SCPD on expand. All members are already-registered singletons;
        // the bundle itself is a stateless singleton threaded into the VM graph by ShellViewModel.
        services.AddSingleton<NodeServices>();

        // Story 5.1 — Diagnostics viewer (FR-041). Same launcher-seam shape as the 2.9/3.2/4.3 blocks.
        // Registered BEFORE the ShellViewModel line so IDiagnosticsLauncher auto-resolves into its ctor.
        // The VM is a singleton (single app-lifetime viewer) bound to the singleton ring sink + the
        // runtime gate. Concrete + interface (dual reg) on the launcher so App.OnLaunched can set ShellWindow.
        services.AddSingleton<DiagnosticsViewModel>();
        services.AddSingleton<DiagnosticsLauncher>();
        services.AddSingleton<IDiagnosticsLauncher>(sp => sp.GetRequiredService<DiagnosticsLauncher>());

        // Story 2.5 — Main window shell ViewModel. Singleton: one window, one ShellViewModel.
        // ShellViewModel owns the AdapterScope lifetime (Amendment A26 migration from App.xaml.cs).
        // DeviceTreeViewModel is constructed by ShellViewModel, not registered separately.
        services.AddSingleton<ShellViewModel>();

        return services;
    }
}
