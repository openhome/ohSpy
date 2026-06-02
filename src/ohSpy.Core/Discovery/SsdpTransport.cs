namespace ohSpy.Core.Discovery;

using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using ohSpy.Core.Diagnostics;
using ohSpy.Core.Models;

/// <summary>
/// Per-adapter SSDP UDP transport (Decision 2). Binds a multicast listener on
/// <c>(adapter, 1900)</c> and an ephemeral search socket on <c>(adapter, 0)</c>,
/// pumps every received datagram into a bounded <c>DropOldest(4096)</c> channel,
/// and issues M-SEARCH on demand. Producer-only: the consumer
/// (<c>DiscoveryService</c>, Story 2.4) owns <see cref="IncomingDatagrams"/>.
/// <para>
/// <c>internal sealed</c> per Pattern 7 — the App composition root registers it
/// behind <see cref="ISsdpTransport"/> (InternalsVisibleTo grants App + Tests access).
/// </para>
/// </summary>
internal sealed class SsdpTransport(IDiagnosticEmitter diag) : ISsdpTransport
{
    private const string SsdpMulticastAddressLiteral = "239.255.255.250";
    private const int SsdpPort = 1900;
    private const int ChannelCapacity = 4096;
    private const int NearFullThreshold = ChannelCapacity * 9 / 10; // 90% (AC-2.1.5)
    private const long TelemetryIntervalTicks = TimeSpan.TicksPerSecond; // rate-limit: 1 Hz

    private static readonly IPAddress SsdpMulticastAddress =
        IPAddress.Parse(SsdpMulticastAddressLiteral);

    private Socket? _multicastSocket;
    private Socket? _searchSocket;
    private IPAddress? _adapterIPv4;
    private Channel<SsdpDatagram>? _channel;
    private CancellationTokenSource? _runCts;
    private Task? _multicastLoop;
    private Task? _searchLoop;

    // Telemetry rate-limit stamps (ticks). Two receive loops write concurrently, so
    // updates go through Interlocked — see MaybeEmitOnce.
    private long _lastNearFullTicks;
    private long _lastOverflowTicks;

    private int _disposed;

    public ChannelReader<SsdpDatagram> IncomingDatagrams =>
        (_channel ?? throw new InvalidOperationException("StartAsync has not been called")).Reader;

    public Task StartAsync(IPAddress adapterIPv4, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(adapterIPv4);
        if (_multicastSocket is not null)
        {
            throw new InvalidOperationException("StartAsync already called");
        }

        // Channel first so the receive loops can post immediately once they spin up.
        _channel = CreateChannel();
        _adapterIPv4 = adapterIPv4;

        // ── Multicast listener: (adapter, 1900) + join 239.255.255.250 ──────────
        // ReuseAddress MUST precede Bind — Windows SSDPSRV already holds *:1900, and
        // the option is ignored if set after Bind (AC-2.1.3 / anti-pattern note).
        var mcast = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        mcast.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        mcast.Bind(new IPEndPoint(adapterIPv4, SsdpPort));
        mcast.SetSocketOption(
            SocketOptionLevel.IP,
            SocketOptionName.AddMembership,
            new MulticastOption(SsdpMulticastAddress, adapterIPv4));
        _multicastSocket = mcast;

        // ── Ephemeral search socket: (adapter, 0), adapter-scoped multicast egress ─
        // MulticastInterface takes a 4-byte big-endian IPv4 address (the address-bytes
        // form); this pins M-SEARCH egress to the chosen adapter (AC-2.1.4 / AC-2.1.6).
        // The try/catch ensures the search socket is disposed if its setup fails after
        // allocation — otherwise the kernel handle would leak to the finalizer queue.
        var search = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            search.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            search.Bind(new IPEndPoint(adapterIPv4, 0));
            search.SetSocketOption(
                SocketOptionLevel.IP,
                SocketOptionName.MulticastInterface,
                adapterIPv4.GetAddressBytes());
        }
        catch
        {
            search.Dispose();
            throw;
        }
        _searchSocket = search;

        // Link the caller's adapter token (Decision 7) to a private CTS so DisposeAsync
        // can tear down even when the caller never cancels.
        _runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Pattern 6: Task.Run is legitimate here — these are long-running background
        // receive loops, not sync-over-async. The loop bodies use real async I/O. The
        // run token is forwarded to Task.Run so a pre-start cancellation is honoured (CA2016).
        var runToken = _runCts.Token;
        _multicastLoop = Task.Run(() => ReceiveLoopAsync(mcast, SsdpSource.Multicast, runToken), runToken);
        _searchLoop = Task.Run(() => ReceiveLoopAsync(search, SsdpSource.SearchResponse, runToken), runToken);

        return Task.CompletedTask;
    }

    public async Task SendMSearchAsync(TimeSpan mx, CancellationToken ct)
    {
        if (_searchSocket is null || _adapterIPv4 is null)
        {
            throw new InvalidOperationException("SendMSearchAsync called before StartAsync");
        }

        var mxSeconds = ClampMxSeconds(mx);
        var payload = BuildMSearchPayload(mxSeconds);
        var destination = new IPEndPoint(SsdpMulticastAddress, SsdpPort);

        await _searchSocket.SendToAsync(payload, SocketFlags.None, destination, ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        // Idempotent: a second call is a no-op (AC-2.1.7).
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        // 1. Signal both receive loops to exit.
        if (_runCts is not null)
        {
            try
            {
                await _runCts.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // CTS already disposed — nothing to cancel.
            }
        }

        // 2. Leave the multicast group cleanly before closing the socket (AC-2.1.7).
        if (_multicastSocket is not null && _adapterIPv4 is not null)
        {
            try
            {
                _multicastSocket.SetSocketOption(
                    SocketOptionLevel.IP,
                    SocketOptionName.DropMembership,
                    new MulticastOption(SsdpMulticastAddress, _adapterIPv4));
            }
            catch (SocketException)
            {
                // Tolerate teardown races — we are shutting down anyway.
            }
            catch (ObjectDisposedException)
            {
                // Socket already closed — nothing to leave.
            }
        }

        // 3. Close both sockets — also unblocks any in-flight ReceiveFromAsync.
        try { _multicastSocket?.Dispose(); } catch { /* teardown race tolerated */ }
        try { _searchSocket?.Dispose(); } catch { /* teardown race tolerated */ }

        // 4. Await loop completion so no background task dangles past dispose. VSTHRD003 is
        // suppressed: these are our own background loops started in StartAsync — awaiting
        // them here is the deliberate teardown join (same pattern as DiagnosticFileSink).
#pragma warning disable VSTHRD003
        if (_multicastLoop is not null)
        {
            try { await _multicastLoop.ConfigureAwait(false); }
            catch { /* loops swallow their own faults per AC-2.1.8 */ }
        }

        if (_searchLoop is not null)
        {
            try { await _searchLoop.ConfigureAwait(false); }
            catch { /* same */ }
        }
#pragma warning restore VSTHRD003

        // 5. Complete the writer so the reader observes the close (AC-2.1.7).
        _channel?.Writer.TryComplete();

        // 6. Dispose the run-CTS.
        _runCts?.Dispose();
    }

    private async Task ReceiveLoopAsync(Socket socket, SsdpSource source, CancellationToken token)
    {
        var writer = _channel!.Writer;

        // 64 KB per loop — typical SSDP datagrams are < 1500 bytes; we allocate
        // generously and copy out only the received bytes so consumers see no slack.
        var buffer = new byte[64 * 1024];
        var endpoint = new IPEndPoint(IPAddress.Any, 0);

        while (!token.IsCancellationRequested)
        {
            SocketReceiveFromResult result;
            try
            {
                result = await socket
                    .ReceiveFromAsync(buffer, SocketFlags.None, endpoint, token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break; // normal shutdown path (AC-2.1.9)
            }
            catch (ObjectDisposedException)
            {
                break; // teardown raced the receive (AC-2.1.7)
            }
            catch (SocketException sx)
            {
                // FR-039 / NFR-R1: one bad packet does not kill the session (AC-2.1.8).
                diag.Warning(
                    DiagCategories.SsdpParse,
                    "ssdp receive failed",
                    new DiagnosticContext { ErrorText = sx.SocketErrorCode.ToString() });

                // Tiny back-off so a hot bad-state cannot pin the CPU.
                try
                {
                    await Task.Delay(50, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                continue;
            }

            var remote = (IPEndPoint)result.RemoteEndPoint;
            var payload = new byte[result.ReceivedBytes];
            Buffer.BlockCopy(buffer, 0, payload, 0, result.ReceivedBytes);

            var datagram = new SsdpDatagram(remote, payload, DateTime.UtcNow, source);

            // Capture count BEFORE the write so overflow detection is meaningful: with
            // DropOldest, TryWrite always succeeds (it drops the oldest item), so only
            // the pre-write count can tell us whether a drop will/did occur (P2 fix).
            var countBeforeWrite = _channel!.Reader.Count;

            // Bounded channel with DropOldest never blocks the writer; TryWrite returns
            // false only once the channel is completed (teardown).
            if (!writer.TryWrite(datagram))
            {
                break;
            }

            EmitChannelFillTelemetry(countBeforeWrite);
        }
    }

    /// <summary>
    /// Emits rate-limited near-full / overflow Warnings (AC-2.1.5). DropOldest does not
    /// raise a drop event; overflow is inferred from the pre-write count: if the channel
    /// was already at capacity before TryWrite, the write displaced the oldest item.
    /// [Amendment A21 candidate: 1 Hz cadence is invented here.]
    /// </summary>
    private void EmitChannelFillTelemetry(int countBeforeWrite)
    {
        if (countBeforeWrite >= ChannelCapacity)
        {
            MaybeEmitOnce(
                ref _lastOverflowTicks,
                DiagCategories.SsdpChannelOverflow,
                "ssdp channel overflow - oldest dropped");
        }
        else if (countBeforeWrite >= NearFullThreshold)
        {
            MaybeEmitOnce(
                ref _lastNearFullTicks,
                DiagCategories.SsdpChannelNearFull,
                "ssdp channel near full");
        }
    }

    private void MaybeEmitOnce(ref long lastEmitTicks, string category, string message)
    {
        var now = Environment.TickCount64 * TimeSpan.TicksPerMillisecond;
        var last = Interlocked.Read(ref lastEmitTicks);
        if (now - last < TelemetryIntervalTicks)
        {
            return;
        }

        // Only one racing loop wins the slot; the loser skips this emission.
        if (Interlocked.CompareExchange(ref lastEmitTicks, now, last) != last)
        {
            return;
        }

        diag.Warning(category, message);
    }

    private static Channel<SsdpDatagram> CreateChannel() =>
        Channel.CreateBounded<SsdpDatagram>(new BoundedChannelOptions(ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

    /// <summary>
    /// Builds the M-SEARCH wire payload exactly per UDA 1.0 §1.2.2. ASCII (SSDP/HTTP
    /// framing is strict ASCII), CRLF line endings, blank-line terminator, quoted MAN.
    /// <c>internal static</c> so tests can assert byte exactness (AC-2.1.6) without sockets.
    /// </summary>
    internal static byte[] BuildMSearchPayload(int mxSeconds)
    {
        var text =
            "M-SEARCH * HTTP/1.1\r\n" +
            "HOST: 239.255.255.250:1900\r\n" +
            "MAN: \"ssdp:discover\"\r\n" +
            $"MX: {mxSeconds}\r\n" +
            "ST: upnp:rootdevice\r\n" +
            "\r\n";
        return Encoding.ASCII.GetBytes(text);
    }

    /// <summary>
    /// Clamps the MX header value to the UDA-mandated minimum of 1 second (AC-2.1.6).
    /// <c>internal static</c> so the clamp is unit-testable without socket egress.
    /// </summary>
    internal static int ClampMxSeconds(TimeSpan mx) => Math.Max(1, (int)mx.TotalSeconds);

    // ── Test seams (InternalsVisibleTo: ohSpy.Core.Tests) ───────────────────────
    internal Task? MulticastReceiveLoop => _multicastLoop;

    internal Task? SearchReceiveLoop => _searchLoop;

    /// <summary>
    /// Runs the receive loop against a caller-supplied socket so the SocketException
    /// resilience path (AC-2.1.8) can be exercised with a deliberately faulted socket.
    /// </summary>
    internal Task RunReceiveLoopForTestAsync(Socket socket, SsdpSource source, CancellationToken token)
    {
        _channel ??= CreateChannel();
        return ReceiveLoopAsync(socket, source, token);
    }
}
