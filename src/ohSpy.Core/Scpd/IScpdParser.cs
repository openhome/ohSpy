namespace ohSpy.Core.Scpd;

using ohSpy.Core.Models;

/// <summary>
/// Parses Service Control Protocol Description (SCPD) XML. Two methods because the two
/// consumers have very different access patterns:
/// <list type="bullet">
///   <item><see cref="StreamActionsAsync"/> — incremental, yields one action at a time.
///   Consumed by service-node expansion (FR-012) where actions should appear in the tree
///   as they parse so a 200-action SCPD doesn't lock the UI (FR-100).</item>
///   <item><see cref="ReadStateTableAsync"/> — fetches the entire state-variable table.
///   Consumed lazily on first invocation-popup open (FR-102 / FR-103) where the caller
///   needs O(1) lookup by argument's <c>RelatedStateVariable</c>.</item>
/// </list>
/// <para>
/// <b>Stream-ownership contract:</b> Neither method disposes the supplied <see cref="Stream"/>.
/// Callers own the stream lifetime (typical pattern: <c>using var ms = new MemoryStream(bytes)</c>).
/// This is enforced by <c>XmlReaderSettings.CloseInput</c> being left at its default <c>false</c>.
/// </para>
/// </summary>
public interface IScpdParser
{
    /// <summary>
    /// Stream actions from the SCPD. The stream awaits <see cref="Task.Yield"/> between
    /// each yielded action so the UI thread can service other work (16 ms per-yield ceiling).
    /// Throws <see cref="Http.UpnpProtocolException"/> wrapping any underlying
    /// <see cref="System.Xml.XmlException"/> on malformed XML / XXE attempt / oversize document.
    /// <para>
    /// The supplied <paramref name="xml"/> must be positioned at the start of the document;
    /// the parser does not seek and does not dispose — caller owns lifetime.
    /// </para>
    /// </summary>
    IAsyncEnumerable<ScpdAction> StreamActionsAsync(Stream xml, CancellationToken ct);

    /// <summary>
    /// Parse the entire state-variable table. Returns a <see cref="ScpdStateTable"/> with
    /// O(1) name lookup. Same exception contract as <see cref="StreamActionsAsync"/>.
    /// Caller owns the stream lifetime — parser does not dispose.
    /// </summary>
    Task<ScpdStateTable> ReadStateTableAsync(Stream xml, CancellationToken ct);
}
