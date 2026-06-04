namespace ohSpy.Core.Tests.Events;

using System.Text;
using FluentAssertions;
using ohSpy.Core.Diagnostics;
using ohSpy.Core.Events;

/// <summary>
/// Story 4.1 — hand-rolled <see cref="HttpRequestParser"/> framing + header discipline
/// (AC-4.1.10..AC-4.1.16). Drives the parser directly over a <see cref="MemoryStream"/> of raw
/// bytes so each framing rule is asserted in isolation, without sockets.
/// </summary>
public sealed class HttpRequestParserTests
{
    private static async Task<HttpRequestParseResult> ParseAsync(string raw, Encoding? enc = null)
    {
        var bytes = (enc ?? Encoding.ASCII).GetBytes(raw);
        using var ms = new MemoryStream(bytes);
        var parser = new HttpRequestParser(ms);
        return await parser.ParseHeadersAsync(CancellationToken.None);
    }

    private const string ValidHeaders =
        "NOTIFY /evt HTTP/1.1\r\nHOST: x\r\nNT: upnp:event\r\nNTS: upnp:propchange\r\nSID: uuid:abc\r\nSEQ: 7\r\nCONTENT-LENGTH: 5\r\n\r\n";

    [Fact]
    [Trait("ac", "AC-4.1.17")]
    public async Task ValidNotify_ParsesFields()
    {
        var result = await ParseAsync(ValidHeaders + "hello");
        var success = result.Should().BeOfType<HttpRequestParseResult.Success>().Subject;

        success.PathAndQuery.Should().Be("/evt");
        success.Sid.Should().Be("uuid:abc");
        success.Seq.Should().Be(7);
        success.ContentLength.Should().Be(5);
    }

    [Fact]
    [Trait("ac", "AC-4.1.13")]
    public async Task BareLf_LineEndings_Accepted()
    {
        var raw = "NOTIFY /evt HTTP/1.1\nSID: s\nCONTENT-LENGTH: 0\n\n";
        var result = await ParseAsync(raw);
        result.Should().BeOfType<HttpRequestParseResult.Success>();
    }

    [Fact]
    [Trait("ac", "AC-4.1.13")]
    public async Task BareCr_InRequestLine_Rejected400()
    {
        // A CR not paired with an LF inside a line → malformed.
        var raw = "NOTIFY /evt\r HTTP/1.1\r\nCONTENT-LENGTH: 0\r\n\r\n";
        var result = await ParseAsync(raw);
        AssertFailure(result, "400 Bad Request", DiagCategories.GenaCallbackMalformed);
    }

    [Fact]
    [Trait("ac", "AC-4.1.13")]
    public async Task ThreeSpace_RequestLine_Rejected400()
    {
        var raw = "NOTIFY /evt extra HTTP/1.1\r\nCONTENT-LENGTH: 0\r\n\r\n";
        var result = await ParseAsync(raw);
        AssertFailure(result, "400 Bad Request", DiagCategories.GenaCallbackMalformed);
    }

    [Fact]
    [Trait("ac", "AC-4.1.13")]
    public async Task LowercaseMethod_Rejected400()
    {
        var raw = "notify /evt HTTP/1.1\r\nCONTENT-LENGTH: 0\r\n\r\n";
        var result = await ParseAsync(raw);
        AssertFailure(result, "400 Bad Request", DiagCategories.GenaCallbackMalformed);
    }

    [Fact]
    [Trait("ac", "AC-4.1.14")]
    public async Task ObsoleteFold_Rejected400()
    {
        var raw = "NOTIFY /evt HTTP/1.1\r\nSID: line1\r\n  folded\r\nCONTENT-LENGTH: 0\r\n\r\n";
        var result = await ParseAsync(raw);
        AssertFailure(result, "400 Bad Request", DiagCategories.GenaCallbackMalformed);
    }

    [Fact]
    [Trait("ac", "AC-4.1.14")]
    public async Task DuplicateKnownHeader_LastWins()
    {
        var raw = "NOTIFY /evt HTTP/1.1\r\nSID: first\r\nSID: second\r\nCONTENT-LENGTH: 0\r\n\r\n";
        var result = await ParseAsync(raw);
        var success = result.Should().BeOfType<HttpRequestParseResult.Success>().Subject;
        success.Sid.Should().Be("second");
    }

    [Fact]
    [Trait("ac", "AC-4.1.14")]
    public async Task HeaderNames_CaseInsensitive()
    {
        var raw = "NOTIFY /evt HTTP/1.1\r\nsId: lower\r\ncOnTeNt-LeNgTh: 0\r\n\r\n";
        var result = await ParseAsync(raw);
        var success = result.Should().BeOfType<HttpRequestParseResult.Success>().Subject;
        success.Sid.Should().Be("lower");
    }

    [Fact]
    [Trait("ac", "AC-4.1.15")]
    public async Task MissingContentLength_Rejected411()
    {
        var raw = "NOTIFY /evt HTTP/1.1\r\nSID: s\r\n\r\n";
        var result = await ParseAsync(raw);
        AssertFailure(result, "411 Length Required", DiagCategories.GenaCallbackNoLength);
    }

    [Fact]
    [Trait("ac", "AC-4.1.15")]
    public async Task DuplicateContentLength_Rejected400_NotLastWins()
    {
        var raw = "NOTIFY /evt HTTP/1.1\r\nCONTENT-LENGTH: 5\r\nCONTENT-LENGTH: 6\r\n\r\n";
        var result = await ParseAsync(raw);
        AssertFailure(result, "400 Bad Request", DiagCategories.GenaCallbackMalformed);
    }

    [Fact]
    [Trait("ac", "AC-4.1.15")]
    public async Task NegativeContentLength_Rejected400()
    {
        var raw = "NOTIFY /evt HTTP/1.1\r\nCONTENT-LENGTH: -1\r\n\r\n";
        var result = await ParseAsync(raw);
        AssertFailure(result, "400 Bad Request", DiagCategories.GenaCallbackMalformed);
    }

    [Fact]
    [Trait("ac", "AC-4.1.16")]
    public async Task ChunkedTransferEncoding_Rejected400()
    {
        var raw = "NOTIFY /evt HTTP/1.1\r\nTRANSFER-ENCODING: chunked\r\nCONTENT-LENGTH: 0\r\n\r\n";
        var result = await ParseAsync(raw);
        AssertFailure(result, "400 Bad Request", DiagCategories.GenaCallbackMalformed);
    }

    [Fact]
    [Trait("ac", "AC-4.1.11")]
    public async Task OversizeBody_ByContentLength_Rejected413_BeforeBuffering()
    {
        // Content-Length declares 2 MB but NO body bytes follow — the parser must reject by the
        // declared length alone, proving it does NOT buffer the body first.
        var raw = "NOTIFY /evt HTTP/1.1\r\nCONTENT-LENGTH: 2097152\r\n\r\n";
        var result = await ParseAsync(raw);
        AssertFailure(result, "413 Content Too Large", DiagCategories.GenaCallbackOversize);
    }

    [Fact]
    [Trait("ac", "AC-4.1.10")]
    public async Task OversizeHeaders_Over16KB_Rejected413()
    {
        var sb = new StringBuilder("NOTIFY /evt HTTP/1.1\r\n");
        // Emit ~20 KB of header bytes with no terminator-before-cap.
        for (var i = 0; i < 400; i++)
        {
            sb.Append("X-Pad-").Append(i).Append(": ").Append(new string('a', 50)).Append("\r\n");
        }

        sb.Append("CONTENT-LENGTH: 0\r\n\r\n");

        var result = await ParseAsync(sb.ToString());
        AssertFailure(result, "413 Content Too Large", DiagCategories.GenaCallbackOversize);
    }

    [Fact]
    [Trait("ac", "AC-4.1.12")]
    public async Task MoreThan64Headers_Rejected400()
    {
        var sb = new StringBuilder("NOTIFY /evt HTTP/1.1\r\n");
        for (var i = 0; i < 70; i++)
        {
            sb.Append("X-H").Append(i).Append(": v\r\n");
        }

        sb.Append("CONTENT-LENGTH: 0\r\n\r\n");

        var result = await ParseAsync(sb.ToString());
        AssertFailure(result, "400 Bad Request", DiagCategories.GenaCallbackMalformed);
    }

    [Fact]
    [Trait("ac", "AC-4.1.2")]
    public async Task AbsentSeq_DefaultsToZero()
    {
        var raw = "NOTIFY /evt HTTP/1.1\r\nSID: s\r\nCONTENT-LENGTH: 0\r\n\r\n";
        var result = await ParseAsync(raw);
        var success = result.Should().BeOfType<HttpRequestParseResult.Success>().Subject;
        success.Seq.Should().Be(0);
    }

    [Fact]
    [Trait("ac", "AC-4.1.2")]
    public async Task UnparseableSeq_DefaultsToZero()
    {
        var raw = "NOTIFY /evt HTTP/1.1\r\nSEQ: not-a-number\r\nCONTENT-LENGTH: 0\r\n\r\n";
        var result = await ParseAsync(raw);
        var success = result.Should().BeOfType<HttpRequestParseResult.Success>().Subject;
        success.Seq.Should().Be(0);
    }

    [Fact]
    [Trait("ac", "AC-4.1.2")]
    public async Task PathAndQuery_SurfacedVerbatim()
    {
        var raw = "NOTIFY /sub/abc?token=xyz HTTP/1.1\r\nCONTENT-LENGTH: 0\r\n\r\n";
        var result = await ParseAsync(raw);
        var success = result.Should().BeOfType<HttpRequestParseResult.Success>().Subject;
        success.PathAndQuery.Should().Be("/sub/abc?token=xyz");
    }

    [Fact]
    [Trait("ac", "AC-4.1.11")]
    public async Task LeftoverBody_CapturedWhenHeadersAndBodyShareARead()
    {
        var bytes = Encoding.ASCII.GetBytes(ValidHeaders + "hello");
        using var ms = new MemoryStream(bytes);
        var parser = new HttpRequestParser(ms);

        var result = await parser.ParseHeadersAsync(CancellationToken.None);
        result.Should().BeOfType<HttpRequestParseResult.Success>();
        Encoding.ASCII.GetString(parser.LeftoverBody).Should().Be("hello");
    }

    private static void AssertFailure(HttpRequestParseResult result, string status, string category)
    {
        var failure = result.Should().BeOfType<HttpRequestParseResult.Failure>().Subject;
        failure.StatusLine.Should().Be(status);
        failure.DiagCategory.Should().Be(category);
    }
}
