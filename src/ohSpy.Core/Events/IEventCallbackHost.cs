namespace ohSpy.Core.Events;

using System.Net;

/// <summary>
/// In-process inbound HTTP/1.1 callback host for GENA <c>NOTIFY</c> events (Story 4.1,
/// Decision 4 L449-454). The FIRST inbound network listener in the product: a raw
/// <see cref="System.Net.Sockets.TcpListener"/> bound to a <em>specific adapter IPv4</em>
/// (NOT <c>0.0.0.0</c>) on an ephemeral port, so it runs unelevated with no <c>http.sys</c>
/// URL ACL (FR-049). Hand-rolled, hardened request parsing: connection cap, per-phase
/// timeouts, size caps, strict framing. Lifecycle (Start/Dispose) is owned by the adapter
/// scope, not the DI container.
/// <para>
/// The interface is <c>public</c> because Story 4.2's <c>SubscriptionClient</c> injects it
/// and reads <see cref="CallbackBaseUrl"/> for the SUBSCRIBE <c>CALLBACK</c> header, and
/// subscribes to <see cref="NotifyReceived"/>.
/// </para>
/// </summary>
public interface IEventCallbackHost : IAsyncDisposable
{
    /// <summary>
    /// Binds a <see cref="System.Net.Sockets.TcpListener"/> on <paramref name="adapterIPv4"/>
    /// (ephemeral port) and starts accepting on a background loop. Returns once the listener
    /// is bound and <see cref="CallbackBaseUrl"/> is populated. A second call throws
    /// <see cref="InvalidOperationException"/>. <paramref name="ct"/> is the adapter token —
    /// its cancellation tears the accept loop and in-flight reads down.
    /// </summary>
    Task StartAsync(IPAddress adapterIPv4, CancellationToken ct);

    /// <summary>
    /// <c>http://&lt;adapterIPv4&gt;:&lt;port&gt;/</c> — announced in the SUBSCRIBE <c>CALLBACK</c>
    /// header (consumed verbatim by <c>IUpnpHttpClient.SubscribeAsync(eventSubUrl, callbackUrl, …)</c>
    /// in Story 4.2). Accessing it before <see cref="StartAsync"/> throws <see cref="InvalidOperationException"/>.
    /// </summary>
    Uri CallbackBaseUrl { get; }

    /// <summary>
    /// Raised for every well-framed <c>NOTIFY</c>. Handlers are <em>awaited</em> (the host
    /// drains in-flight handler tasks on shutdown). The host returns <c>200 OK</c> regardless
    /// of whether any handler matched the SID (an unsubscribed-from-our-side NOTIFY is still an
    /// idempotent ack — it never 404s). A throwing handler yields <c>500</c> for that one
    /// connection and never takes down the accept loop.
    /// </summary>
    event Func<NotifyRequest, Task> NotifyReceived;
}
