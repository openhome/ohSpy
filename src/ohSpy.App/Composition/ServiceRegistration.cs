namespace ohSpy.App.Composition;

using Microsoft.Extensions.DependencyInjection;
using ohSpy.App.Windowing;
using ohSpy.Core.Diagnostics;
using ohSpy.Core.Http;
using ohSpy.Core.Scpd;
using ohSpy.Core.Threading;

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

        // Story 1.3 — minimal diagnostic surface; Story 1.5 will REPLACE this with the
        // production DiagnosticEmitter + ring/file sinks.
        services.AddSingleton<IDiagnosticEmitter, NoOpDiagnosticEmitter>();

        // Story 1.4 — XML parsers (Decision 5). Stateless across documents; singleton fine.
        services.AddSingleton<IScpdParser, XmlReaderScpdParser>();
        services.AddSingleton<IDeviceDescriptionParser, DeviceDescriptionParser>();

        return services;
    }
}
