namespace ohSpy.App.Composition;

using Microsoft.Extensions.DependencyInjection;
using ohSpy.App.Windowing;
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

        return services;
    }
}
