namespace ohSpy.Core.Http;

/// <summary>
/// Result of a successful SUBSCRIBE or RENEW. <see cref="Sid"/> is the subscription
/// identifier from the response's <c>SID:</c> header; <see cref="Timeout"/> is parsed
/// from the <c>TIMEOUT: Second-N</c> header.
/// </summary>
/// <param name="Sid">Subscription identifier (e.g. <c>uuid:abcd-1234-...</c>).</param>
/// <param name="Timeout">
/// Granted lease duration from the device — consumers must RENEW before this expires.
/// <b>NOT</b> the request timeout budget. See <see cref="HttpTimeoutOptions.GenaSubscribe"/>
/// for the per-request budget that bounds the SUBSCRIBE call itself.
/// </param>
public sealed record SubscribeResponse(string Sid, TimeSpan Timeout);
