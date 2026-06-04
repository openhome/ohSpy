namespace ohSpy.Core.Events;

using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Options;
using ohSpy.Core.Diagnostics;
using ohSpy.Core.Http;

/// <summary>
/// In-process inbound HTTP/1.1 callback host for GENA <c>NOTIFY</c> events — the FIRST inbound
/// network listener in the product (Story 4.1, Decision 4 L406-475). A raw
/// <see cref="TcpListener"/> bound to a <em>specific adapter IPv4</em> on an ephemeral port (NOT
/// <c>0.0.0.0</c>), so it runs unelevated with no <c>http.sys</c> URL ACL (FR-049). Hardened:
/// connection cap 8, 5+5 s header/body idle budgets (slowloris), 16 KB header / 1 MB body caps
/// (body-bomb), strict framing.
/// <para>
/// Canonical shape mirrors <c>SsdpTransport</c> (background loop on <see cref="Task.Run"/>, private
/// CTS linked to the adapter token, idempotent budgeted <see cref="DisposeAsync"/>) and
/// <c>UpnpHttpClient</c> (<see cref="IOptions{T}"/> + <see cref="IDiagnosticEmitter"/> ctor).
/// <c>internal sealed</c> per Pattern 7 — DI registers <see cref="IEventCallbackHost"/>.
/// </para>
/// </summary>
internal sealed class EventCallbackHost : IEventCallbackHost
{
    private const int MaxConcurrentConnections = 8;   // AC-4.1.6 — connection cap (flood defence)
    private const int AcceptBacklog = 16;             // AC-4.1.3
    private static readonly TimeSpan DrainBudget = TimeSpan.FromSeconds(2); // AC-4.1.22 (FR-050-aligned)

    private readonly HttpTimeoutOptions _opts;
    private readonly IDiagnosticEmitter _diag;
    private readonly TimeSpan _drainBudget;

    // In-flight handler tasks (per-connection) tracked so DisposeAsync can drain them.
    private readonly ConcurrentDictionary<Task, byte> _connections = new();

    private SemaphoreSlim? _slots;
    private TcpListener? _listener;
    private CancellationTokenSource? _runCts;
    private Task? _acceptLoop;
    private Uri? _callbackBaseUrl;
    private int _started;
    private int _disposed;

    public EventCallbackHost(IOptions<HttpTimeoutOptions> options, IDiagnosticEmitter diag)
        : this(options, diag, DrainBudget)
    {
    }

    /// <summary>Test seam: injectable drain budget so the budget-exceeded force-close path
    /// (AC-4.1.22) is testable without a real 2 s wait.</summary>
    internal EventCallbackHost(IOptions<HttpTimeoutOptions> options, IDiagnosticEmitter diag, TimeSpan drainBudget)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(diag);
        _opts = options.Value;
        _diag = diag;
        _drainBudget = drainBudget;
    }

    public Uri CallbackBaseUrl =>
        _callbackBaseUrl ?? throw new InvalidOperationException("StartAsync has not been called");

    public event Func<NotifyRequest, Task>? NotifyReceived;

    public Task StartAsync(IPAddress adapterIPv4, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(adapterIPv4);

        // Idempotent-start guard (mirror SsdpTransport): a second call throws.
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            throw new InvalidOperationException("StartAsync already called");
        }

        // AC-4.1.3 — bind the SPECIFIC adapter IP + ephemeral port (NOT IPAddress.Any/0.0.0.0).
        var listener = new TcpListener(new IPEndPoint(adapterIPv4, 0));
        listener.Start(AcceptBacklog);
        _listener = listener;

        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _callbackBaseUrl = new Uri($"http://{FormatHost(adapterIPv4)}:{port.ToString(CultureInfo.InvariantCulture)}/");

        _slots = new SemaphoreSlim(MaxConcurrentConnections, MaxConcurrentConnections);

        // Link the caller's adapter token (D7) to a private CTS so DisposeAsync can tear down even
        // when the caller never cancels.
        _runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Pattern 6: Task.Run is legitimate for a long-running accept loop (real async I/O inside),
        // same justification + pragma as SsdpTransport. The run token is forwarded so a pre-start
        // cancellation is honoured (CA2016).
        var runToken = _runCts.Token;
        _acceptLoop = Task.Run(() => AcceptLoopAsync(listener, runToken), runToken);

        return Task.CompletedTask;
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break; // normal shutdown (AC-4.1.21)
            }
            catch (ObjectDisposedException)
            {
                break; // listener.Stop() raced the accept (teardown)
            }
            catch (SocketException)
            {
                // A transient accept error must not kill the loop (one bad connection ≠ session death).
                continue;
            }

            // AC-4.1.6 — connection cap. Try to take a slot WITHOUT blocking; a 9th concurrent
            // connection finds no free slot → accept-then-immediately-close + Flood warning.
            // Wait(0) is a non-blocking zero-timeout try-acquire (returns immediately, never blocks),
            // so VSTHRD103 ("Wait synchronously blocks") does not apply; CancellationToken.None is
            // deliberate (the gate take is instantaneous, nothing to cancel) — CA2016 acknowledged.
#pragma warning disable VSTHRD103, CA2016
            if (_slots!.Wait(0, CancellationToken.None))
#pragma warning restore VSTHRD103, CA2016
            {
                TrackConnection(client, token);
            }
            else
            {
                EmitWarning(client, DiagCategories.GenaCallbackFlood, "connection cap reached — refused", null);
                SafeClose(client);
            }
        }
    }

    private void TrackConnection(TcpClient client, CancellationToken token)
    {
        // Materialise the handler task, register it for drain, and self-deregister on completion.
        Task handler = null!;
        handler = Task.Run(async () =>
        {
            try
            {
                await HandleConnectionAsync(client, token).ConfigureAwait(false);
            }
            finally
            {
                try { _slots!.Release(); } catch (ObjectDisposedException) { /* semaphore disposed by DisposeAsync force-close */ }
                _connections.TryRemove(handler, out _);
            }
        }, token);

        _connections.TryAdd(handler, 0);

        // Tiny race: if the handler finished before TryAdd ran, remove the now-stale entry.
        if (handler.IsCompleted)
        {
            _connections.TryRemove(handler, out _);
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken token)
    {
        var remote = client.Client.RemoteEndPoint as IPEndPoint;
        var remoteText = remote?.ToString();

        try
        {
            using (client)
            {
                var raw = client.GetStream();
                await using var stream = new TimeoutStream(raw, _opts.CallbackHeaders, leaveOpen: true);

                var parser = new HttpRequestParser(stream);

                HttpRequestParseResult parsed;
                try
                {
                    parsed = await parser.ParseHeadersAsync(token).ConfigureAwait(false);
                }
                catch (CallbackTimeoutException)
                {
                    // AC-4.1.7 — headers stalled beyond the budget → close + HeadersTo (no HTTP response).
                    EmitWarning(remoteText, DiagCategories.GenaCallbackHeadersTo, "header read budget exceeded", null);
                    return;
                }

                if (parsed is HttpRequestParseResult.Failure failure)
                {
                    EmitWarning(remoteText, failure.DiagCategory, failure.Reason, null);
                    await WriteStatusResponseAsync(raw, failure.StatusLine, token).ConfigureAwait(false);
                    return;
                }

                var success = (HttpRequestParseResult.Success)parsed;

                // AC-4.1.8 — switch the active budget to the body budget for the body read.
                stream.ActiveBudget = _opts.CallbackBody;

                byte[] body;
                try
                {
                    body = await ReadBodyAsync(stream, parser.LeftoverBody, success.ContentLength, token).ConfigureAwait(false);
                }
                catch (CallbackTimeoutException)
                {
                    // Body stalled (or shorter than the declared Content-Length) → close + BodyTo.
                    EmitWarning(remoteText, DiagCategories.GenaCallbackBodyTo, "body read budget exceeded", null);
                    return;
                }

                // AC-4.1.17 — valid NOTIFY: build the raw request, raise+await handlers, return 200.
                var request = new NotifyRequest(success.Sid, success.Seq, success.PathAndQuery, body, DateTime.UtcNow);

                try
                {
                    await DispatchAsync(request).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // AC-4.1.19 — a faulting handler yields 500 for THIS connection only; the accept
                    // loop and other connections are unaffected (catch is per-connection).
                    EmitWarning(remoteText, DiagCategories.GenaCallbackMalformed, "internal dispatch error", ex.ToString());
                    await WriteStatusResponseAsync(raw, "500 Internal Server Error", token).ConfigureAwait(false);
                    return;
                }

                // AC-4.1.17 — Verbose-only success diagnostic carrying the SID.
                _diag.Verbose(
                    DiagCategories.GenaNotifyReceived,
                    "GENA NOTIFY received",
                    new DiagnosticContext { Sid = string.IsNullOrEmpty(success.Sid) ? null : success.Sid, RemoteEndpoint = remoteText });

                await WriteStatusResponseAsync(raw, "200 OK", token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown path (AC-4.1.21) — swallow, no diagnostic.
        }
        catch (ObjectDisposedException)
        {
            // Teardown raced the connection — swallow.
        }
        catch (IOException)
        {
            // Peer reset / write-after-close during a normal close. Not actionable.
        }
        catch (SocketException)
        {
            // Same: transport-level teardown noise.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Last-resort per-connection guard — a bug in framing must never kill the accept loop.
            EmitWarning(remoteText, DiagCategories.GenaCallbackMalformed, "unexpected connection error", ex.ToString());
        }
    }

    /// <summary>Awaits every subscribed <see cref="NotifyReceived"/> handler (AC-4.1.17). With no
    /// subscriber it is an idempotent ack — the host still returns 200 (AC-4.1.18).</summary>
    private async Task DispatchAsync(NotifyRequest request)
    {
        var handler = NotifyReceived;
        if (handler is null)
        {
            return; // unknown / no-subscriber SID → idempotent 200, no special-casing (AC-4.1.18)
        }

        foreach (var invocation in handler.GetInvocationList().Cast<Func<NotifyRequest, Task>>())
        {
            await invocation(request).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reads exactly <paramref name="contentLength"/> body bytes (AC-4.1.11), starting from any
    /// <paramref name="leftover"/> bytes the header read over-consumed. A short body (peer sends
    /// fewer than declared) manifests as a read that never completes → the body budget fires →
    /// <see cref="CallbackTimeoutException"/> → BodyTo (AC-4.1.8). Extra bytes on the wire past
    /// Content-Length are ignored (no keep-alive).
    /// </summary>
    private static async Task<byte[]> ReadBodyAsync(Stream stream, byte[] leftover, int contentLength, CancellationToken ct)
    {
        var body = new byte[contentLength];
        var offset = 0;

        if (leftover.Length > 0)
        {
            var take = Math.Min(leftover.Length, contentLength);
            Array.Copy(leftover, 0, body, 0, take);
            offset = take;
        }

        while (offset < contentLength)
        {
            var read = await stream.ReadAsync(body.AsMemory(offset, contentLength - offset), ct).ConfigureAwait(false);
            if (read == 0)
            {
                // EOF before the declared length — treat the same as a body stall (peer underflowed).
                throw new CallbackTimeoutException("connection closed before the declared Content-Length was read");
            }

            offset += read;
        }

        return body;
    }

    /// <summary>Writes a minimal, single-request HTTP/1.1 response with <c>Connection: close</c>
    /// and an empty body (AC-4.1.5/AC-4.1.17), then flushes.</summary>
    private static async Task WriteStatusResponseAsync(Stream raw, string statusLine, CancellationToken ct)
    {
        var response =
            $"HTTP/1.1 {statusLine}\r\n" +
            "Content-Length: 0\r\n" +
            "Connection: close\r\n" +
            "\r\n";
        var bytes = Encoding.ASCII.GetBytes(response);

        try
        {
            await raw.WriteAsync(bytes, ct).ConfigureAwait(false);
            await raw.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException or OperationCanceledException)
        {
            // The peer may have already gone; we are closing the connection regardless.
        }
    }

    public async ValueTask DisposeAsync()
    {
        // AC-4.1.22 — idempotent (Interlocked guard).
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        // 1. Cancel the private CTS — unblocks in-flight reads.
        if (_runCts is not null)
        {
            try
            {
                await _runCts.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // Already disposed — tolerated during teardown.
            }
        }

        // 2. Stop the listener — unblocks the pending AcceptTcpClientAsync.
        try { _listener?.Stop(); } catch { /* teardown race tolerated */ }

        // 3. Await the accept loop's exit (our own background task — VSTHRD003 suppressed, the
        //    deliberate teardown join, same as SsdpTransport).
#pragma warning disable VSTHRD003
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.ConfigureAwait(false); }
            catch { /* the loop swallows its own faults */ }
        }
#pragma warning restore VSTHRD003

        // 4. Drain in-flight connection + NotifyReceived handler tasks within the 2 s budget.
        //    On timeout, the connections are force-closed (their sockets are disposed via cancel +
        //    listener.Stop already) and we log — mirror AdapterScope.DisposeAsync's WaitAsync shape.
        var inFlight = _connections.Keys.ToArray();
        if (inFlight.Length > 0)
        {
#pragma warning disable VSTHRD003
            try
            {
                await Task.WhenAll(inFlight).WaitAsync(_drainBudget).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Drain overran the budget → force-close (the connections' sockets are already being
                // torn down by the CTS cancel + listener.Stop above) and log. There is no dedicated
                // Gena.Callback.* constant for this rare operational condition (the pre-added set has
                // no drain-timeout category, and AC-4.1.20 forbids adding one). GenaCallbackFlood is
                // the closest fit — both signal callback-host resource pressure (connections not
                // clearing). The adapter-layer caller (AdapterScope.DisposeAsync) additionally logs
                // AdapterSwitchTimeout if the whole teardown overruns its own 2 s budget. [Open Q]
                // AC-4.1.20: every Gena.Callback.* Warning must carry a DiagnosticContext. Drain-overrun
                // is a host-level event (no specific remote), so RemoteEndpoint is genuinely unknown — pass
                // an empty context rather than omitting it entirely, to stay structurally consistent with Pattern 11.
                _diag.Warning(DiagCategories.GenaCallbackFlood, "callback host drain exceeded budget — forcing close", new DiagnosticContext());
            }
            catch
            {
                // Individual handlers swallow their own faults; a residual fault here is teardown noise.
            }
#pragma warning restore VSTHRD003
        }

        // 5. Dispose the gate + CTS.
        _slots?.Dispose();
        _runCts?.Dispose();
    }

    private void EmitWarning(TcpClient client, string category, string message, string? errorText)
    {
        var remote = (client.Client?.RemoteEndPoint as IPEndPoint)?.ToString();
        EmitWarning(remote, category, message, errorText);
    }

    /// <summary>Every hardening/error Warning carries <c>RemoteEndpoint</c> (Pattern 11 / AC-4.1.20).
    /// <c>DeviceUuid</c> is unknown at the host layer (it sees only IP:port) → left null.</summary>
    private void EmitWarning(string? remoteEndpoint, string category, string message, string? errorText) =>
        _diag.Warning(category, message, new DiagnosticContext { RemoteEndpoint = remoteEndpoint, ErrorText = errorText });

    private static void SafeClose(TcpClient client)
    {
        try { client.Close(); } catch { /* already gone */ }
        client.Dispose();
    }

    /// <summary>IPv6 literals must be bracketed in an authority; IPv4 is used as-is.</summary>
    private static string FormatHost(IPAddress ip) =>
        ip.AddressFamily == AddressFamily.InterNetworkV6
            ? $"[{ip}]"
            : ip.ToString();

    // ── Test seams (InternalsVisibleTo: ohSpy.Core.Tests) ───────────────────────
    internal Task? AcceptLoop => _acceptLoop;

    internal int InFlightConnectionCount => _connections.Count;
}
