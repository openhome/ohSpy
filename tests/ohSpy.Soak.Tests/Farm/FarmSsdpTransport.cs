namespace ohSpy.Soak.Tests.Farm;

using System.Net;
using System.Threading.Channels;
using ohSpy.Core.Discovery;
using ohSpy.Core.Models;

/// <summary>
/// Story 6.2 — the writable <see cref="ISsdpTransport"/> the soak harness hands to the real
/// <c>AdapterScope</c> via the transport factory. The farm's SSDP advertiser writes per-device
/// <c>NOTIFY ssdp:alive</c> / <c>ssdp:byebye</c> datagrams into this channel; the real
/// <c>DiscoveryService</c> reads them through the scope-owned <see cref="IncomingDatagrams"/>
/// reader, exactly as a live transport would deliver real multicast traffic.
/// <para>
/// Mirrors the shipped <c>ohSpy.Core.Tests</c> <c>ChannelSsdpTransport</c> (capacity 256,
/// DropOldest) — the same injection mechanism, soak-scoped. <see cref="StartAsync"/> /
/// <see cref="SendMSearchAsync"/> are no-ops (no real sockets); an M-SEARCH simply triggers the
/// harness's advertiser to re-emit alives (the farm handles that out-of-band).
/// </para>
/// </summary>
internal sealed class FarmSsdpTransport : ISsdpTransport
{
    private readonly Channel<SsdpDatagram> _channel =
        Channel.CreateBounded<SsdpDatagram>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

    private int _mSearchCount;

    public ChannelReader<SsdpDatagram> IncomingDatagrams => _channel.Reader;

    /// <summary>Number of M-SEARCHes issued (startup + each rescan) — lets the harness re-burst alives.</summary>
    public int MSearchCount => Volatile.Read(ref _mSearchCount);

    /// <summary>Raised when the Core issues an M-SEARCH (startup / rescan) so the farm can respond with
    /// a fresh alive burst, exactly as real devices answer a search.</summary>
    public event Action? MSearchIssued;

    /// <summary>Feed a datagram into the channel for the DiscoveryService to process.</summary>
    public ValueTask WriteAsync(SsdpDatagram datagram, CancellationToken ct = default) =>
        _channel.Writer.WriteAsync(datagram, ct);

    public void Complete() => _channel.Writer.TryComplete();

    public Task StartAsync(IPAddress adapterIPv4, CancellationToken ct) => Task.CompletedTask;

    public Task SendMSearchAsync(TimeSpan mx, CancellationToken ct)
    {
        Interlocked.Increment(ref _mSearchCount);
        MSearchIssued?.Invoke();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Complete();
        return ValueTask.CompletedTask;
    }
}
