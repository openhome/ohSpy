namespace ohSpy.Core.Events;

using ohSpy.Core.Devices;
using ohSpy.Core.Models;

/// <summary>
/// Orchestrates the full GENA subscription lifecycle (Story 4.2, AC-4.2.1; epic L1591-1594):
/// SUBSCRIBE → auto-renew before timeout → UNSUBSCRIBE on close, routing inbound <c>NOTIFY</c>s to
/// subscribers by SID and parsing each <c>&lt;e:propertyset&gt;</c> into an <see cref="EventNotification"/>.
/// <para>
/// The FIRST consumer of both the Story 1.3 GENA verbs (<c>IUpnpHttpClient.SubscribeAsync</c> /
/// <c>RenewSubscriptionAsync</c> / <c>UnsubscribeAsync</c>) and the Story 4.1 callback-host seam
/// (<c>IEventCallbackHost.CallbackBaseUrl</c> + <c>NotifyReceived</c>). Abstract behind this seam so
/// Story 4.3's popup VM can inject it (mirrors every other Core seam — <c>IUpnpHttpClient</c>,
/// <c>IEventCallbackHost</c>). Registered as a <b>singleton</b> (epic L1668-1670).
/// </para>
/// </summary>
public interface ISubscriptionClient
{
    /// <summary>
    /// Provides the per-<c>AdapterScope</c> token to the singleton client (the DI singleton cannot
    /// inject it at construction). Called from <c>ShellViewModel.RunStartAsync</c> immediately after
    /// <c>IEventCallbackHost.StartAsync</c>. The adapter token is the D7 "level above" used for the
    /// UNSUBSCRIBE-on-active-close (AC-4.2.13) and is linked into every renew loop so an adapter
    /// switch cascades a lapse into all live subscriptions (AC-4.2.15). Story 5.2's atomic rebind
    /// re-calls this with the new adapter's token.
    /// </summary>
    void SetAdapterContext(CancellationToken adapterToken);

    /// <summary>
    /// SUBSCRIBE to <paramref name="service"/> on <paramref name="parentEntry"/> and start the
    /// lifecycle (auto-renew + SID routing). On success returns a live <see cref="SubscriptionHandle"/>.
    /// On a failed SUBSCRIBE the thrown <c>UpnpException</c> propagates and NO subscription is created
    /// (no SID, hence no UNSUBSCRIBE — AC-4.2.10). <paramref name="popupToken"/> is the D7 popup-level
    /// token; closing it cancels the renew loop (the popup-close path).
    /// </summary>
    Task<SubscriptionHandle> SubscribeAsync(
        ServiceDescription service, RegistryEntry parentEntry, CancellationToken popupToken);
}
