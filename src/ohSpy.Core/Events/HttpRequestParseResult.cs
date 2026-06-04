namespace ohSpy.Core.Events;

using ohSpy.Core.Diagnostics;

/// <summary>
/// The discriminated outcome of <see cref="HttpRequestParser.ParseHeadersAsync"/>: either a
/// <see cref="Success"/> carrying the framed fields (so the host can read the body then dispatch),
/// or a <see cref="Failure"/> carrying the HTTP status + the matching
/// <see cref="DiagCategories"/> constant, so the host maps a rejected request to a response and a
/// diagnostic in ONE place (Decision 4 L420-433 — "emit a parse-result discriminated outcome").
/// </summary>
internal abstract record HttpRequestParseResult
{
    private HttpRequestParseResult() { }

    /// <summary>Well-framed request line + headers. The body has NOT been read yet — the host reads
    /// exactly <see cref="ContentLength"/> bytes under the body budget.</summary>
    public sealed record Success(string PathAndQuery, string Sid, long Seq, int ContentLength) : HttpRequestParseResult;

    /// <summary>A hardening/framing violation. <see cref="StatusLine"/> is the bare HTTP status
    /// reason (e.g. <c>"400 Bad Request"</c>); <see cref="DiagCategory"/> is the pre-added
    /// <c>Gena.Callback.*</c> constant; <see cref="Reason"/> is the human note placed in
    /// <c>ErrorText</c>.</summary>
    public sealed record Failure(string StatusLine, string DiagCategory, string Reason) : HttpRequestParseResult;
}
