namespace ohSpy.Core.Events;

/// <summary>
/// A single inbound GENA <c>NOTIFY</c> callback, as received and framed by the
/// <see cref="IEventCallbackHost"/> (Story 4.1, Decision 4 L456-461). This is the RAW
/// hand-off across the 4.1 → 4.2/4.3 boundary: the host surfaces the request-target,
/// the <c>SID</c>/<c>SEQ</c> headers and the body <em>bytes</em> only — it does NOT
/// parse the <c>&lt;e:propertyset&gt;</c> XML (that is Story 4.2's <c>SubscriptionClient</c>,
/// FR-104). Downstream consumers (4.2 routes by SID, 4.3 renders) own the body parse.
/// </summary>
/// <param name="Sid">The subscription identifier from the <c>SID</c> header (verbatim, may be empty if absent).</param>
/// <param name="Seq">The event sequence number from the <c>SEQ</c> header; absent/unparseable → <c>0</c> (lenient — some stacks omit it on the initial event).</param>
/// <param name="PathAndQuery">The request-target exactly as it arrived on the request line (verbatim); 4.2 may read back a per-subscription token embedded in the CALLBACK path.</param>
/// <param name="Body">The body bytes, read exactly to the declared <c>Content-Length</c>. Never an XML model.</param>
/// <param name="ReceivedUtc">The host's UTC arrival timestamp.</param>
public sealed record NotifyRequest(string Sid, long Seq, string PathAndQuery, byte[] Body, DateTime ReceivedUtc);
