namespace ohSpy.Core.Events;

using System.Globalization;
using System.Text;
using ohSpy.Core.Diagnostics;

/// <summary>
/// Hand-rolled, hardened HTTP/1.1 request-header parser for the GENA callback host
/// (Decision 4 L420-433). <b>Strict framing, lenient headers:</b> the strict rules close the
/// real threat surface (slowloris, body-bombs, malformed framing) while header tolerance
/// absorbs legitimate vendor quirks (case, ordering, extras). It reads the request line and the
/// header block byte-by-byte from a <see cref="TimeoutStream"/> (so the AC-4.1.7 header budget
/// applies per read), enforces the 16 KB header / 64-header caps, and resolves
/// <c>Content-Length</c> / <c>Transfer-Encoding</c>. It does NOT read the body — the host reads
/// exactly <see cref="HttpRequestParseResult.Success.ContentLength"/> bytes under the body budget.
/// <para>
/// Over-read tolerance: a single <see cref="ReadAsync"/> may return body bytes past the empty-CRLF
/// terminator. Those are buffered in <see cref="LeftoverBody"/> and handed to the host so they are
/// not lost (the host prepends them before reading the remaining body).
/// </para>
/// </summary>
internal sealed class HttpRequestParser
{
    internal const int MaxHeaderBytes = 16 * 1024;   // AC-4.1.10 — 16 KB header block cap
    internal const int MaxHeaderCount = 64;          // AC-4.1.12 — 64 header lines
    internal const int MaxBodyBytes = 1_048_576;     // AC-4.1.11 — 1 MB inbound body cap (D4 constant; NOT MaxGenaResponseBytes)
    private const int ReadChunk = 4096;

    private static readonly HttpRequestParseResult.Failure BadRequestLine =
        new("400 Bad Request", DiagCategories.GenaCallbackMalformed, "malformed request line");

    private readonly Stream _stream;
    private readonly byte[] _buffer = new byte[ReadChunk];
    private int _bufferStart;
    private int _bufferEnd;

    /// <summary>Body bytes that arrived in the same read as the header terminator. The host
    /// consumes these first (they belong to the body, not the headers).</summary>
    public byte[] LeftoverBody { get; private set; } = [];

    public HttpRequestParser(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;
    }

    /// <summary>
    /// Parses the request line + header block, returning a discriminated success/failure result.
    /// Reads through the supplied <paramref name="ct"/> (cancellation = shutdown). Idle-read
    /// overrun surfaces as <see cref="CallbackTimeoutException"/> from the <see cref="TimeoutStream"/>;
    /// the host catches it and maps to the headers-timeout diagnostic.
    /// </summary>
    public async Task<HttpRequestParseResult> ParseHeadersAsync(CancellationToken ct)
    {
        // 1. Read the raw header block up to (and including) the empty-line terminator, capping at 16 KB.
        var headerBytes = await ReadHeaderBlockAsync(ct).ConfigureAwait(false);
        if (headerBytes is null)
        {
            // Either EOF before any terminator, or the 16 KB cap was exceeded.
            return _oversizeHeaders
                ? new HttpRequestParseResult.Failure("413 Content Too Large", DiagCategories.GenaCallbackOversize, "header block exceeded 16 KB")
                : new HttpRequestParseResult.Failure("400 Bad Request", DiagCategories.GenaCallbackMalformed, "connection closed before headers complete");
        }

        // The header block is strict ASCII per RFC 7230 §3.2.4; decode as Latin1 so every byte
        // round-trips 1:1 (no multibyte surprises) and split on the CRLF/LF line boundaries.
        var text = Encoding.Latin1.GetString(headerBytes);
        var lines = SplitLines(text, out var bareCrSeen);
        if (bareCrSeen)
        {
            // AC-4.1.13 — bare CR (a CR not part of a CRLF) is rejected.
            return BadRequestLine;
        }

        if (lines.Count == 0)
        {
            return BadRequestLine;
        }

        // 2. Request line — exactly two SP, uppercase ASCII method (AC-4.1.13).
        if (!TryParseRequestLine(lines[0], out var pathAndQuery))
        {
            return BadRequestLine;
        }

        // 3. Header lines (everything after the request line, before the terminating empty line).
        var headerLines = lines.Count - 1;
        if (headerLines > MaxHeaderCount)
        {
            // AC-4.1.12 — too many headers → malformed framing.
            return new HttpRequestParseResult.Failure("400 Bad Request", DiagCategories.GenaCallbackMalformed, "more than 64 header lines");
        }

        string? sid = null;
        string? seqRaw = null;
        long? contentLength = null;
        var contentLengthSeen = 0;
        var transferEncodingSeen = false;

        for (var i = 1; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Length == 0)
            {
                continue; // tolerated trailing blank inside the captured block
            }

            // AC-4.1.14 / RFC 7230 §3.2.4 — obsolete line folding (a header line that begins with
            // SP or HTAB is a continuation of the previous header) is rejected.
            if (line[0] is ' ' or '\t')
            {
                return new HttpRequestParseResult.Failure("400 Bad Request", DiagCategories.GenaCallbackMalformed, "obsolete header folding");
            }

            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                return new HttpRequestParseResult.Failure("400 Bad Request", DiagCategories.GenaCallbackMalformed, "header line missing name:value");
            }

            // Case-insensitive name → lowercase canonical (AC-4.1.14). Value is OWS-trimmed.
            var name = line[..colon].ToLowerInvariant();
            var value = line[(colon + 1)..].Trim();

            switch (name)
            {
                case "sid":
                    sid = value;       // duplicate known header → last-wins (AC-4.1.14)
                    break;
                case "seq":
                    seqRaw = value;    // last-wins
                    break;
                case "content-length":
                    contentLengthSeen++;
                    if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var cl) || cl < 0)
                    {
                        return new HttpRequestParseResult.Failure("400 Bad Request", DiagCategories.GenaCallbackMalformed, "non-numeric or negative Content-Length");
                    }

                    contentLength = cl;
                    break;
                case "transfer-encoding":
                    transferEncodingSeen = true;
                    break;
                default:
                    break; // unknown header — ignored (already counted against the 64 cap)
            }
        }

        // 4. Transfer-Encoding (chunked or otherwise) is rejected — chunked is out of v1 (AC-4.1.16).
        if (transferEncodingSeen)
        {
            return new HttpRequestParseResult.Failure("400 Bad Request", DiagCategories.GenaCallbackMalformed, "Transfer-Encoding not supported (chunked rejected)");
        }

        // 5. Content-Length discipline (AC-4.1.15).
        if (contentLengthSeen > 1)
        {
            // Duplicate Content-Length is strict (NOT last-wins) — request smuggling defence.
            return new HttpRequestParseResult.Failure("400 Bad Request", DiagCategories.GenaCallbackMalformed, "duplicate Content-Length");
        }

        if (contentLength is null)
        {
            return new HttpRequestParseResult.Failure("411 Length Required", DiagCategories.GenaCallbackNoLength, "Content-Length required");
        }

        // 6. Body size cap — reject by Content-Length BEFORE the host buffers anything (AC-4.1.11).
        if (contentLength.Value > MaxBodyBytes)
        {
            return new HttpRequestParseResult.Failure("413 Content Too Large", DiagCategories.GenaCallbackOversize, "Content-Length exceeded 1 MB");
        }

        var seq = ParseSeq(seqRaw);
        return new HttpRequestParseResult.Success(pathAndQuery, sid ?? string.Empty, seq, (int)contentLength.Value);
    }

    private bool _oversizeHeaders;

    /// <summary>
    /// Reads bytes until the CRLF-CRLF (or LF-LF) end-of-headers terminator, returning the header
    /// block (terminator included). Returns <c>null</c> on EOF-before-terminator or on exceeding the
    /// 16 KB cap (<see cref="_oversizeHeaders"/> distinguishes the two for the caller). Any body bytes
    /// in the same read past the terminator are stashed in <see cref="LeftoverBody"/>.
    /// </summary>
    private async Task<byte[]?> ReadHeaderBlockAsync(CancellationToken ct)
    {
        var acc = new List<byte>(ReadChunk);

        while (true)
        {
            // Drain any buffered bytes first (carried over from a previous chunk read).
            while (_bufferStart < _bufferEnd)
            {
                var b = _buffer[_bufferStart++];
                acc.Add(b);

                if (acc.Count > MaxHeaderBytes)
                {
                    _oversizeHeaders = true;
                    return null;
                }

                if (IsHeaderTerminator(acc))
                {
                    // Stash leftover body bytes already in the buffer.
                    if (_bufferStart < _bufferEnd)
                    {
                        LeftoverBody = _buffer[_bufferStart.._bufferEnd];
                        _bufferStart = _bufferEnd;
                    }

                    return [.. acc];
                }
            }

            // Refill.
            var read = await _stream.ReadAsync(_buffer.AsMemory(0, ReadChunk), ct).ConfigureAwait(false);
            if (read == 0)
            {
                return null; // EOF before the terminator
            }

            _bufferStart = 0;
            _bufferEnd = read;
        }
    }

    /// <summary>True once the accumulator ends in CRLF-CRLF or the LF-LF degenerate form.</summary>
    private static bool IsHeaderTerminator(List<byte> acc)
    {
        var n = acc.Count;

        // CRLF CRLF
        if (n >= 4 && acc[n - 4] == (byte)'\r' && acc[n - 3] == (byte)'\n'
                   && acc[n - 2] == (byte)'\r' && acc[n - 1] == (byte)'\n')
        {
            return true;
        }

        // LF LF (bare-LF line endings accepted per AC-4.1.13)
        if (n >= 2 && acc[n - 2] == (byte)'\n' && acc[n - 1] == (byte)'\n')
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Splits the header block into lines on LF, stripping a trailing CR (CRLF) from each. A bare
    /// CR anywhere other than immediately before an LF sets <paramref name="bareCrSeen"/> (→ 400).
    /// The terminating empty line(s) are dropped.
    /// </summary>
    private static List<string> SplitLines(string text, out bool bareCrSeen)
    {
        bareCrSeen = false;
        var lines = new List<string>();
        var start = 0;

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                var end = i;
                if (end > start && text[end - 1] == '\r')
                {
                    end--; // strip the CR of a CRLF
                }

                var line = text[start..end];

                // A bare CR inside the line content (not the line-ending CR) is illegal.
                if (line.Contains('\r', StringComparison.Ordinal))
                {
                    bareCrSeen = true;
                }

                if (line.Length == 0 && lines.Count > 0)
                {
                    break; // empty line terminates the header block
                }

                lines.Add(line);
                start = i + 1;
            }
        }

        return lines;
    }

    /// <summary>
    /// Request line: <c>METHOD SP request-target SP HTTP-version</c> — exactly two SP, uppercase
    /// ASCII token method (AC-4.1.13). Surfaces the request-target verbatim (AC-4.1.2 / open Q#4).
    /// </summary>
    private static bool TryParseRequestLine(string line, out string pathAndQuery)
    {
        pathAndQuery = string.Empty;

        // Exactly two spaces → exactly three parts.
        var parts = line.Split(' ');
        if (parts.Length != 3)
        {
            return false;
        }

        var method = parts[0];
        var target = parts[1];
        var version = parts[2];

        if (method.Length == 0 || !IsUppercaseAsciiToken(method))
        {
            return false;
        }

        if (target.Length == 0)
        {
            return false;
        }

        // Be lenient about the exact version token but require the HTTP/ prefix shape.
        if (!version.StartsWith("HTTP/", StringComparison.Ordinal))
        {
            return false;
        }

        pathAndQuery = target;
        return true;
    }

    private static bool IsUppercaseAsciiToken(string s)
    {
        foreach (var c in s)
        {
            // Method is an RFC 7230 token; UPnP NOTIFY/M-SEARCH etc. are uppercase letters + a hyphen.
            var ok = (c >= 'A' && c <= 'Z') || c == '-';
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>SEQ leniency (AC-4.1.2 / open Q#3): absent/unparseable → 0, never a 400.</summary>
    private static long ParseSeq(string? raw) =>
        long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var seq) ? seq : 0L;
}
