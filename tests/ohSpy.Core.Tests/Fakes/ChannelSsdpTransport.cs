namespace ohSpy.Core.Tests.Fakes;

using System.Net;
using System.Threading.Channels;
using ohSpy.Core.Discovery;
using ohSpy.Core.Models;

/// <summary>
/// Writable-channel transport fake for DiscoveryService integration tests. Distinct from
/// <see cref="FakeSsdpTransport"/> which has capacity 1 and no <c>WriteAsync</c> method.
/// </summary>
internal sealed class ChannelSsdpTransport : ISsdpTransport
{
    private readonly Channel<SsdpDatagram> _channel =
        Channel.CreateBounded<SsdpDatagram>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

    public ChannelReader<SsdpDatagram> IncomingDatagrams => _channel.Reader;

    /// <summary>Feed a datagram into the channel for the DiscoveryService to process.</summary>
    public ValueTask WriteAsync(SsdpDatagram datagram, CancellationToken ct = default) =>
        _channel.Writer.WriteAsync(datagram, ct);

    /// <summary>Completes the channel so the read loop exits cleanly.</summary>
    public void Complete() => _channel.Writer.Complete();

    public Task StartAsync(IPAddress adapterIPv4, CancellationToken ct) => Task.CompletedTask;
    public Task SendMSearchAsync(TimeSpan mx, CancellationToken ct) => Task.CompletedTask;
    public ValueTask DisposeAsync() { Complete(); return ValueTask.CompletedTask; }
}
