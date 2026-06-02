namespace ohSpy.Core.Tests.Fakes;

using System.Net;
using System.Threading.Channels;
using ohSpy.Core.Discovery;
using ohSpy.Core.Models;

/// <summary>
/// Records <see cref="ISsdpTransport"/> lifecycle calls so <c>AdapterScope</c> tests
/// can assert bind IP, M-SEARCH MX, and dispose count without real sockets. An
/// optional <see cref="TeardownDelay"/> drives the FR-050 budget-exceeded path.
/// </summary>
internal sealed class FakeSsdpTransport : ISsdpTransport
{
    private readonly Channel<SsdpDatagram> _channel = Channel.CreateBounded<SsdpDatagram>(1);

    public IPAddress? StartedWith { get; private set; }
    public TimeSpan? MSearchMx { get; private set; }
    public int StartCallCount { get; private set; }
    public int MSearchCallCount { get; private set; }
    public int DisposeCallCount { get; private set; }

    /// <summary>When &gt; 0, <see cref="DisposeAsync"/> delays this long (budget test).</summary>
    public TimeSpan TeardownDelay { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// Cancel this to unblock a slow <see cref="DisposeAsync"/> after the test has finished
    /// its assertions, preventing the delay from leaking as a background continuation.
    /// </summary>
    public CancellationTokenSource TeardownCts { get; } = new();

    public ChannelReader<SsdpDatagram> IncomingDatagrams => _channel.Reader;

    public Task StartAsync(IPAddress adapterIPv4, CancellationToken ct)
    {
        StartCallCount++;
        StartedWith = adapterIPv4;
        return Task.CompletedTask;
    }

    public Task SendMSearchAsync(TimeSpan mx, CancellationToken ct)
    {
        MSearchCallCount++;
        MSearchMx = mx;
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        DisposeCallCount++;
        if (TeardownDelay > TimeSpan.Zero)
        {
            try
            {
                await Task.Delay(TeardownDelay, TeardownCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Test cancelled the delay after asserting — expected, not an error.
            }
        }
    }
}
