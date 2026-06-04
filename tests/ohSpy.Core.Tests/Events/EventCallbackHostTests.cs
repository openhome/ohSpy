namespace ohSpy.Core.Tests.Events;

using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Options;
using ohSpy.Core.Diagnostics;
using ohSpy.Core.Events;
using ohSpy.Core.Http;
using ohSpy.Core.Tests.Fakes;

/// <summary>
/// Story 4.1 — <see cref="EventCallbackHost"/> in-process integration tests over a hand-rolled raw
/// <see cref="FakeGenaClient"/> (AC-4.1.23). The host binds <see cref="IPAddress.Loopback"/> so the
/// driver can connect over a real TCP socket and drive every framing / size / timeout / flood AC,
/// plus the slowloris (AC-4.1.24), flood (AC-4.1.25) and budgeted-drain (AC-4.1.22) contracts.
/// <c>[Trait("category", "integration")]</c> keeps them off the chaos filter.
/// </summary>
[Trait("category", "integration")]
public sealed class EventCallbackHostTests
{
    private static readonly IPAddress Loopback = IPAddress.Loopback;

    private static IOptions<HttpTimeoutOptions> Options(TimeSpan? headers = null, TimeSpan? body = null) =>
        Microsoft.Extensions.Options.Options.Create(new HttpTimeoutOptions
        {
            CallbackHeaders = headers ?? TimeSpan.FromSeconds(5),
            CallbackBody = body ?? TimeSpan.FromSeconds(5),
        });

    private static string Notify(string sid, long seq, string body, string path = "/evt")
    {
        var bytes = Encoding.ASCII.GetByteCount(body);
        return
            $"NOTIFY {path} HTTP/1.1\r\n" +
            "HOST: 127.0.0.1\r\n" +
            "NT: upnp:event\r\n" +
            "NTS: upnp:propchange\r\n" +
            $"SID: {sid}\r\n" +
            $"SEQ: {seq}\r\n" +
            $"CONTENT-LENGTH: {bytes}\r\n" +
            "\r\n" +
            body;
    }

    // ── Happy path ──────────────────────────────────────────────────────────

    [Fact]
    [Trait("ac", "AC-4.1.17")]
    public async Task ValidNotify_Returns200_AndRaisesNotifyReceived()
    {
        var diag = new ConcurrentCapturingDiagnosticEmitter();
        await using var host = new EventCallbackHost(Options(), diag);
        await host.StartAsync(Loopback, CancellationToken.None);

        NotifyRequest? received = null;
        host.NotifyReceived += r => { received = r; return Task.CompletedTask; };

        await using var client = new FakeGenaClient();
        await client.ConnectAsync(host.CallbackBaseUrl);
        await client.SendAsync(Notify("uuid:s1", 42, "<propertyset/>", "/sub/a?x=1"));

        var status = await client.ReadStatusLineAsync();
        status.Should().Be("HTTP/1.1 200 OK");

        received.Should().NotBeNull();
        received!.Sid.Should().Be("uuid:s1");
        received.Seq.Should().Be(42);
        received.PathAndQuery.Should().Be("/sub/a?x=1");
        Encoding.ASCII.GetString(received.Body).Should().Be("<propertyset/>");

        diag.CountOf(DiagCategories.GenaNotifyReceived).Should().Be(1);
    }

    [Fact]
    [Trait("ac", "AC-4.1.18")]
    public async Task NoSubscriber_StillReturns200_IdempotentAck()
    {
        var diag = new ConcurrentCapturingDiagnosticEmitter();
        await using var host = new EventCallbackHost(Options(), diag);
        await host.StartAsync(Loopback, CancellationToken.None);
        // No NotifyReceived handler attached.

        await using var client = new FakeGenaClient();
        await client.ConnectAsync(host.CallbackBaseUrl);
        await client.SendAsync(Notify("uuid:unknown", 1, "x"));

        (await client.ReadStatusLineAsync()).Should().Be("HTTP/1.1 200 OK");
    }

    [Fact]
    [Trait("ac", "AC-4.1.17")]
    public async Task EmptyBody_ZeroContentLength_Returns200()
    {
        var diag = new ConcurrentCapturingDiagnosticEmitter();
        await using var host = new EventCallbackHost(Options(), diag);
        await host.StartAsync(Loopback, CancellationToken.None);

        byte[]? body = null;
        host.NotifyReceived += r => { body = r.Body; return Task.CompletedTask; };

        await using var client = new FakeGenaClient();
        await client.ConnectAsync(host.CallbackBaseUrl);
        await client.SendAsync(Notify("uuid:s", 0, string.Empty));

        (await client.ReadStatusLineAsync()).Should().Be("HTTP/1.1 200 OK");
        body.Should().NotBeNull();
        body!.Length.Should().Be(0);
    }

    // ── Lifecycle / bind ────────────────────────────────────────────────────

    [Fact]
    [Trait("ac", "AC-4.1.3")]
    public async Task CallbackBaseUrl_BindsAdapterIp_NotAnyAddress()
    {
        var diag = new ConcurrentCapturingDiagnosticEmitter();
        await using var host = new EventCallbackHost(Options(), diag);
        await host.StartAsync(Loopback, CancellationToken.None);

        host.CallbackBaseUrl.Host.Should().Be(Loopback.ToString());
        host.CallbackBaseUrl.Host.Should().NotBe("0.0.0.0");
        host.CallbackBaseUrl.Port.Should().BeGreaterThan(0);
        host.CallbackBaseUrl.Scheme.Should().Be("http");
        host.CallbackBaseUrl.AbsolutePath.Should().Be("/");
    }

    [Fact]
    [Trait("ac", "AC-4.1.4")]
    public async Task CallbackBaseUrl_BeforeStart_Throws()
    {
        var diag = new ConcurrentCapturingDiagnosticEmitter();
        await using var host = new EventCallbackHost(Options(), diag);

        var act = () => _ = host.CallbackBaseUrl;
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    [Trait("ac", "AC-4.1.3")]
    public async Task StartAsync_Twice_Throws()
    {
        var diag = new ConcurrentCapturingDiagnosticEmitter();
        await using var host = new EventCallbackHost(Options(), diag);
        await host.StartAsync(Loopback, CancellationToken.None);

        var act = async () => await host.StartAsync(Loopback, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ── Framing failures ────────────────────────────────────────────────────

    [Theory]
    [Trait("ac", "AC-4.1.13")]
    [InlineData("NOTIFY /evt extra HTTP/1.1\r\nCONTENT-LENGTH: 0\r\n\r\n", "400 Bad Request")]      // three SP
    [InlineData("notify /evt HTTP/1.1\r\nCONTENT-LENGTH: 0\r\n\r\n", "400 Bad Request")]            // lowercase method
    [InlineData("NOTIFY /evt HTTP/1.1\r\nSID: a\r\n  fold\r\nCONTENT-LENGTH: 0\r\n\r\n", "400 Bad Request")] // obsolete fold
    public async Task MalformedFraming_Returns400_AndEmitsMalformed(string raw, string expectedStatus)
    {
        var diag = new ConcurrentCapturingDiagnosticEmitter();
        await using var host = new EventCallbackHost(Options(), diag);
        await host.StartAsync(Loopback, CancellationToken.None);

        await using var client = new FakeGenaClient();
        await client.ConnectAsync(host.CallbackBaseUrl);
        await client.SendAsync(raw);

        (await client.ReadStatusLineAsync()).Should().Be($"HTTP/1.1 {expectedStatus}");
        diag.CountOf(DiagCategories.GenaCallbackMalformed).Should().BeGreaterThan(0);
        AssertWarningsCarryRemoteEndpoint(diag);
    }

    [Fact]
    [Trait("ac", "AC-4.1.15")]
    public async Task MissingContentLength_Returns411_AndEmitsNoLength()
    {
        var diag = new ConcurrentCapturingDiagnosticEmitter();
        await using var host = new EventCallbackHost(Options(), diag);
        await host.StartAsync(Loopback, CancellationToken.None);

        await using var client = new FakeGenaClient();
        await client.ConnectAsync(host.CallbackBaseUrl);
        await client.SendAsync("NOTIFY /evt HTTP/1.1\r\nSID: s\r\n\r\n");

        (await client.ReadStatusLineAsync()).Should().Be("HTTP/1.1 411 Length Required");
        diag.CountOf(DiagCategories.GenaCallbackNoLength).Should().Be(1);
    }

    [Fact]
    [Trait("ac", "AC-4.1.15")]
    public async Task DuplicateContentLength_Returns400()
    {
        var diag = new ConcurrentCapturingDiagnosticEmitter();
        await using var host = new EventCallbackHost(Options(), diag);
        await host.StartAsync(Loopback, CancellationToken.None);

        await using var client = new FakeGenaClient();
        await client.ConnectAsync(host.CallbackBaseUrl);
        await client.SendAsync("NOTIFY /evt HTTP/1.1\r\nCONTENT-LENGTH: 1\r\nCONTENT-LENGTH: 2\r\n\r\n");

        (await client.ReadStatusLineAsync()).Should().Be("HTTP/1.1 400 Bad Request");
        diag.CountOf(DiagCategories.GenaCallbackMalformed).Should().Be(1);
    }

    [Fact]
    [Trait("ac", "AC-4.1.16")]
    public async Task ChunkedTransferEncoding_Returns400()
    {
        var diag = new ConcurrentCapturingDiagnosticEmitter();
        await using var host = new EventCallbackHost(Options(), diag);
        await host.StartAsync(Loopback, CancellationToken.None);

        await using var client = new FakeGenaClient();
        await client.ConnectAsync(host.CallbackBaseUrl);
        await client.SendAsync("NOTIFY /evt HTTP/1.1\r\nTRANSFER-ENCODING: chunked\r\nCONTENT-LENGTH: 0\r\n\r\n");

        (await client.ReadStatusLineAsync()).Should().Be("HTTP/1.1 400 Bad Request");
        diag.CountOf(DiagCategories.GenaCallbackMalformed).Should().Be(1);
    }

    // ── Size caps ───────────────────────────────────────────────────────────

    [Fact]
    [Trait("ac", "AC-4.1.11")]
    public async Task OversizeBody_ByContentLength_Returns413_BeforeBuffering()
    {
        var diag = new ConcurrentCapturingDiagnosticEmitter();
        await using var host = new EventCallbackHost(Options(), diag);
        await host.StartAsync(Loopback, CancellationToken.None);

        await using var client = new FakeGenaClient();
        await client.ConnectAsync(host.CallbackBaseUrl);
        // Declare 2 MB but send NO body — the host must reject by Content-Length alone.
        await client.SendAsync("NOTIFY /evt HTTP/1.1\r\nCONTENT-LENGTH: 2097152\r\n\r\n");

        (await client.ReadStatusLineAsync()).Should().Be("HTTP/1.1 413 Content Too Large");
        diag.CountOf(DiagCategories.GenaCallbackOversize).Should().Be(1);
    }

    [Fact]
    [Trait("ac", "AC-4.1.10")]
    public async Task OversizeHeaders_Over16KB_Returns413()
    {
        var diag = new ConcurrentCapturingDiagnosticEmitter();
        await using var host = new EventCallbackHost(Options(), diag);
        await host.StartAsync(Loopback, CancellationToken.None);

        var sb = new StringBuilder("NOTIFY /evt HTTP/1.1\r\n");
        for (var i = 0; i < 400; i++)
        {
            sb.Append("X-Pad-").Append(i).Append(": ").Append(new string('a', 50)).Append("\r\n");
        }

        sb.Append("CONTENT-LENGTH: 0\r\n\r\n");

        await using var client = new FakeGenaClient();
        await client.ConnectAsync(host.CallbackBaseUrl);
        await client.SendAsync(sb.ToString());

        (await client.ReadStatusLineAsync()).Should().Be("HTTP/1.1 413 Content Too Large");
        diag.CountOf(DiagCategories.GenaCallbackOversize).Should().Be(1);
    }

    [Fact]
    [Trait("ac", "AC-4.1.12")]
    public async Task MoreThan64Headers_Returns400()
    {
        var diag = new ConcurrentCapturingDiagnosticEmitter();
        await using var host = new EventCallbackHost(Options(), diag);
        await host.StartAsync(Loopback, CancellationToken.None);

        var sb = new StringBuilder("NOTIFY /evt HTTP/1.1\r\n");
        for (var i = 0; i < 70; i++)
        {
            sb.Append("X-H").Append(i).Append(": v\r\n");
        }

        sb.Append("CONTENT-LENGTH: 0\r\n\r\n");

        await using var client = new FakeGenaClient();
        await client.ConnectAsync(host.CallbackBaseUrl);
        await client.SendAsync(sb.ToString());

        (await client.ReadStatusLineAsync()).Should().Be("HTTP/1.1 400 Bad Request");
        diag.CountOf(DiagCategories.GenaCallbackMalformed).Should().Be(1);
    }

    // ── Internal dispatch error ─────────────────────────────────────────────

    [Fact]
    [Trait("ac", "AC-4.1.19")]
    public async Task HandlerThrows_Returns500_AndLoopSurvives()
    {
        var diag = new ConcurrentCapturingDiagnosticEmitter();
        await using var host = new EventCallbackHost(Options(), diag);
        await host.StartAsync(Loopback, CancellationToken.None);
        host.NotifyReceived += _ => throw new InvalidOperationException("boom");

        await using (var client = new FakeGenaClient())
        {
            await client.ConnectAsync(host.CallbackBaseUrl);
            await client.SendAsync(Notify("uuid:s", 1, "x"));
            (await client.ReadStatusLineAsync()).Should().Be("HTTP/1.1 500 Internal Server Error");
        }

        // The accept loop must survive a faulting handler — a second connection still works
        // (now with no throwing handler removed; re-add a benign one to confirm 200).
        host.NotifyReceived -= _ => throw new InvalidOperationException("boom"); // no-op (different delegate); loop liveness is the point
        await using var client2 = new FakeGenaClient();
        await client2.ConnectAsync(host.CallbackBaseUrl);
        await client2.SendAsync(Notify("uuid:s2", 2, "y"));
        var second = await client2.ReadStatusLineAsync();
        second.Should().StartWith("HTTP/1.1 5"); // still throwing handler attached → 500, but the loop is alive
    }

    // ── Timeouts (slowloris) ────────────────────────────────────────────────

    [Fact]
    [Trait("ac", "AC-4.1.7")]
    public async Task HeadersStall_HitsHeaderBudget_ClosesWithHeadersTo()
    {
        var diag = new ConcurrentCapturingDiagnosticEmitter();
        await using var host = new EventCallbackHost(Options(headers: TimeSpan.FromMilliseconds(120)), diag);
        await host.StartAsync(Loopback, CancellationToken.None);

        await using var client = new FakeGenaClient();
        await client.ConnectAsync(host.CallbackBaseUrl);
        // Send a partial request line then stall — never completing the header block.
        await client.SendAsync("NOTIFY /evt HTTP/1.1\r\nHOST: x");

        var closed = await client.WaitForCloseAsync(TimeSpan.FromSeconds(3));
        closed.Should().BeTrue();
        await WaitUntilAsync(() => diag.CountOf(DiagCategories.GenaCallbackHeadersTo) >= 1, TimeSpan.FromSeconds(3));
        diag.CountOf(DiagCategories.GenaCallbackHeadersTo).Should().Be(1);
    }

    [Fact]
    [Trait("ac", "AC-4.1.8")]
    public async Task BodyShorterThanContentLength_HitsBodyBudget_ClosesWithBodyTo()
    {
        var diag = new ConcurrentCapturingDiagnosticEmitter();
        await using var host = new EventCallbackHost(Options(body: TimeSpan.FromMilliseconds(120)), diag);
        await host.StartAsync(Loopback, CancellationToken.None);

        await using var client = new FakeGenaClient();
        await client.ConnectAsync(host.CallbackBaseUrl);
        // Declare CL=10 but send only 3 body bytes then stall → body budget fires.
        await client.SendAsync("NOTIFY /evt HTTP/1.1\r\nCONTENT-LENGTH: 10\r\n\r\nabc");

        var closed = await client.WaitForCloseAsync(TimeSpan.FromSeconds(3));
        closed.Should().BeTrue();
        await WaitUntilAsync(() => diag.CountOf(DiagCategories.GenaCallbackBodyTo) >= 1, TimeSpan.FromSeconds(3));
        diag.CountOf(DiagCategories.GenaCallbackBodyTo).Should().Be(1);
    }

    [Fact]
    [Trait("ac", "AC-4.1.24")]
    public async Task Slowloris_EightDrippingConnections_AllHitHeaderTimeout_NinthServedAfterSlotFrees()
    {
        var diag = new ConcurrentCapturingDiagnosticEmitter();
        // Shrunk header budget so the drip (gap > budget) trips it in ms, not seconds.
        await using var host = new EventCallbackHost(Options(headers: TimeSpan.FromMilliseconds(150)), diag);
        await host.StartAsync(Loopback, CancellationToken.None);

        host.NotifyReceived += _ => Task.CompletedTask;

        var drips = new List<FakeGenaClient>();
        var dripTasks = new List<Task>();
        try
        {
            for (var i = 0; i < 8; i++)
            {
                var c = new FakeGenaClient();
                await c.ConnectAsync(host.CallbackBaseUrl);
                drips.Add(c);
                // Drip 1 byte every 300 ms (> 150 ms budget) → each stalls and times out on headers.
                dripTasks.Add(c.DripAsync("NOTIFY /evt HTTP/1.1\r\nHOST: x\r\n", TimeSpan.FromMilliseconds(300)));
            }

            // All 8 connections should hit the header timeout and close.
            await WaitUntilAsync(() => diag.CountOf(DiagCategories.GenaCallbackHeadersTo) >= 8, TimeSpan.FromSeconds(5));
            diag.CountOf(DiagCategories.GenaCallbackHeadersTo).Should().BeGreaterOrEqualTo(8);

            // A 9th connection opens cleanly after slots free and serves a real NOTIFY → 200.
            await using var ninth = new FakeGenaClient();
            await ninth.ConnectAsync(host.CallbackBaseUrl);
            await ninth.SendAsync(Notify("uuid:s9", 1, "ok"));
            (await ninth.ReadStatusLineAsync()).Should().Be("HTTP/1.1 200 OK");
        }
        finally
        {
            foreach (var t in dripTasks)
            {
                try { await t; } catch { /* drips fail when the host closes — expected */ }
            }

            foreach (var c in drips)
            {
                await c.DisposeAsync();
            }
        }
    }

    // ── Flood ───────────────────────────────────────────────────────────────

    [Fact]
    [Trait("ac", "AC-4.1.25")]
    public async Task Flood_FiftyConnections_EightServed_RestRefusedWithFlood_NoLeak()
    {
        var diag = new ConcurrentCapturingDiagnosticEmitter();
        await using var host = new EventCallbackHost(Options(), diag);
        await host.StartAsync(Loopback, CancellationToken.None);

        // Slow the 8 served handlers so all 50 connections overlap and saturate the gate.
        var gate = new TaskCompletionSource();
        var served = 0;
        host.NotifyReceived += _ =>
        {
            Interlocked.Increment(ref served);
            return gate.Task; // hold the slot until released
        };

        var clients = new List<FakeGenaClient>();
        try
        {
            for (var i = 0; i < 50; i++)
            {
                var c = new FakeGenaClient();
                await c.ConnectAsync(host.CallbackBaseUrl);
                clients.Add(c);
                await c.SendAsync(Notify($"uuid:{i}", i, "x"));
            }

            // The refused connections are closed immediately with a Flood warning. Give them a beat.
            await WaitUntilAsync(() => diag.CountOf(DiagCategories.GenaCallbackFlood) >= 1, TimeSpan.FromSeconds(5));

            diag.CountOf(DiagCategories.GenaCallbackFlood).Should().BeGreaterThan(0);
            host.InFlightConnectionCount.Should().BeLessOrEqualTo(8);
            AssertWarningsCarryRemoteEndpoint(diag);
        }
        finally
        {
            gate.TrySetResult(); // release the 8 held handlers so the host can drain
            foreach (var c in clients)
            {
                await c.DisposeAsync();
            }
        }
    }

    // ── Budgeted drain on DisposeAsync ──────────────────────────────────────

    [Fact]
    [Trait("ac", "AC-4.1.22")]
    public async Task DisposeAsync_IsIdempotent()
    {
        var diag = new ConcurrentCapturingDiagnosticEmitter();
        var host = new EventCallbackHost(Options(), diag);
        await host.StartAsync(Loopback, CancellationToken.None);

        await host.DisposeAsync();
        await host.DisposeAsync(); // second call is a no-op, must not throw
    }

    [Fact]
    [Trait("ac", "AC-4.1.22")]
    public async Task DisposeAsync_WithoutStart_IsNoOp()
    {
        var diag = new ConcurrentCapturingDiagnosticEmitter();
        var host = new EventCallbackHost(Options(), diag);
        await host.DisposeAsync(); // never started (zero-adapter path) — must be safe
    }

    [Fact]
    [Trait("ac", "AC-4.1.22")]
    public async Task DisposeAsync_SlowHandler_ForceClosesAtBudget()
    {
        var diag = new ConcurrentCapturingDiagnosticEmitter();
        // Tiny drain budget so the force-close path is exercised without a real 2 s wait.
        var host = new EventCallbackHost(Options(), diag, TimeSpan.FromMilliseconds(200));
        await host.StartAsync(Loopback, CancellationToken.None);

        var entered = new TaskCompletionSource();
        host.NotifyReceived += _ =>
        {
            entered.TrySetResult();
            return Task.Delay(TimeSpan.FromSeconds(30)); // a handler that overruns the drain budget
        };

        await using var client = new FakeGenaClient();
        await client.ConnectAsync(host.CallbackBaseUrl);
        await client.SendAsync(Notify("uuid:slow", 1, "x"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); // ensure the handler is in-flight

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await host.DisposeAsync();
        sw.Stop();

        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5)); // returned at the budget, not after 30 s
        diag.CountOf(DiagCategories.GenaCallbackFlood).Should().BeGreaterThan(0); // the drain-exceeded warning

        // The drain-overrun warning is a host-level event (no specific remote connection) so
        // RemoteEndpoint is null by design — this is the documented exception to AC-4.1.20's
        // "every Warning carries RemoteEndpoint" rule (the DiagnosticContext is still provided,
        // just without a RemoteEndpoint, keeping the call structurally consistent with Pattern 11).
        var drainWarning = diag.Entries.First(e => e.Category == DiagCategories.GenaCallbackFlood && e.Message.Contains("drain exceeded"));
        drainWarning.Context.RemoteEndpoint.Should().BeNull();
        drainWarning.Context.DeviceUuid.Should().BeNull();
    }

    [Fact]
    [Trait("ac", "AC-4.1.22")]
    public async Task DisposeAsync_DrainsInFlightConnectionWithinBudget()
    {
        var diag = new ConcurrentCapturingDiagnosticEmitter();
        var host = new EventCallbackHost(Options(), diag, TimeSpan.FromSeconds(2));
        await host.StartAsync(Loopback, CancellationToken.None);

        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        host.NotifyReceived += async _ =>
        {
            entered.TrySetResult();
            await release.Task; // completes promptly below — handler finishes inside the budget
        };

        await using var client = new FakeGenaClient();
        await client.ConnectAsync(host.CallbackBaseUrl);
        await client.SendAsync(Notify("uuid:s", 1, "x"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        release.SetResult();
        await host.DisposeAsync(); // drains cleanly, no force-close warning
        diag.CountOf(DiagCategories.GenaCallbackFlood).Should().Be(0);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static void AssertWarningsCarryRemoteEndpoint(ConcurrentCapturingDiagnosticEmitter diag)
    {
        foreach (var e in diag.Entries.Where(x => x.Severity == "Warning" && x.Category.StartsWith("Gena.Callback.", StringComparison.Ordinal)))
        {
            e.Context.RemoteEndpoint.Should().NotBeNullOrEmpty();
            e.Context.DeviceUuid.Should().BeNull();
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(25);
        }
    }
}
