namespace ohSpy.Core.Http;

/// <summary>
/// Typed facade over a single shared <see cref="HttpClient"/> for all UPnP outbound HTTP.
/// Every method bakes a per-request timeout (via linked CTS) and a per-response size cap
/// into the call — there is no way for a consumer to accidentally inherit
/// <see cref="HttpClient.Timeout"/>'s 100 s default or to skip the size guard. This is
/// the structural antidote to the prior tool's "slow devices hang the app" defect.
/// </summary>
/// <remarks>
/// All Fetch methods return <c>byte[]</c> — parsing is a separate concern (Story 1.4 / D5
/// revision). The architecture's original D3 text shows <c>FetchDeviceDescriptionAsync</c>
/// returning <c>Task&lt;DeviceDescription&gt;</c>; that is corrected here to mirror
/// <c>FetchScpdAsync</c>'s raw-bytes return for symmetry. See Dev Notes for the
/// architecture-amendment recommendation.
/// </remarks>
public interface IUpnpHttpClient
{
    /// <summary>
    /// GET the device description XML from <paramref name="locationUrl"/> (the SSDP
    /// <c>LOCATION</c> header). Returns raw bytes; parsing is the caller's concern
    /// (typically <c>IDeviceDescriptionParser</c> from Story 1.4).
    /// </summary>
    Task<byte[]> FetchDeviceDescriptionAsync(Uri locationUrl, CancellationToken ct);

    /// <summary>
    /// GET the service control protocol description (SCPD) XML from <paramref name="scpdUrl"/>.
    /// Returns raw bytes; incremental parsing is the caller's concern
    /// (<c>IScpdParser.StreamActionsAsync</c> from Story 1.4 + FR-100).
    /// </summary>
    Task<byte[]> FetchScpdAsync(Uri scpdUrl, CancellationToken ct);

    /// <summary>
    /// POST a SOAP action envelope to a service's control URL. Returns the response
    /// envelope on 200 OK; throws <see cref="UpnpFaultException"/> on 500 + structured
    /// <c>&lt;s:Fault&gt;</c> body.
    /// </summary>
    Task<SoapResponse> InvokeActionAsync(SoapRequest request, CancellationToken ct);

    /// <summary>
    /// Send a SUBSCRIBE request to a service's eventSubURL with the given callback URL.
    /// Returns the granted subscription on success.
    /// </summary>
    Task<SubscribeResponse> SubscribeAsync(
        Uri eventSubUrl, Uri callbackUrl, TimeSpan requestedTimeout, CancellationToken ct);

    /// <summary>
    /// Renew an existing subscription identified by <paramref name="sid"/>. Returns
    /// the updated lease on success.
    /// </summary>
    Task<SubscribeResponse> RenewSubscriptionAsync(
        Uri eventSubUrl, string sid, TimeSpan requestedTimeout, CancellationToken ct);

    /// <summary>
    /// Tear down a subscription identified by <paramref name="sid"/>. Best-effort —
    /// fire-and-forget on popup close. Throws on transport/timeout failure so callers
    /// can decide whether to retry.
    /// </summary>
    Task UnsubscribeAsync(Uri eventSubUrl, string sid, CancellationToken ct);
}
