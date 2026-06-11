namespace ohSpy.Core.Tests.Fakes;

using System.Collections.Concurrent;
using System.Net;
using System.Threading.Channels;
using ohSpy.Core.Discovery;
using ohSpy.Core.Events;
using ohSpy.Core.Models;

/// <summary>
/// Shared ordered event log for the Story 5.2 atomic-rebind tests. The recording transport / callback
/// host fakes append their lifecycle calls here so a test can assert the FR-050 10-step ORDER across
/// the scope + host (e.g. "old transport disposed BEFORE new transport started"). Thread-safe append.
/// </summary>
internal sealed class SwitchRecorder
{
    private readonly ConcurrentQueue<string> _events = new();

    public void Record(string evt) => _events.Enqueue(evt);

    public IReadOnlyList<string> Events => _events.ToArray();

    /// <summary>Index of the first event whose text contains <paramref name="needle"/> (or -1).</summary>
    public int IndexOf(string needle)
    {
        var arr = Events;
        for (var i = 0; i < arr.Count; i++)
        {
            if (arr[i].Contains(needle, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }
}

/// <summary>
/// <see cref="ISsdpTransport"/> fake that records its lifecycle (start IP, M-SEARCH, dispose) into a
/// shared <see cref="SwitchRecorder"/> with a per-instance tag, so the 10-step ORDER is assertable.
/// Each instance owns a tiny writable channel so a test can feed datagrams to the rebound read loop.
/// </summary>
internal sealed class RecordingSsdpTransport : ISsdpTransport
{
    private readonly SwitchRecorder _rec;
    private readonly string _tag;
    private readonly Channel<SsdpDatagram> _channel =
        Channel.CreateBounded<SsdpDatagram>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

    public RecordingSsdpTransport(SwitchRecorder rec, string tag)
    {
        _rec = rec;
        _tag = tag;
    }

    public IPAddress? StartedWith { get; private set; }
    public int StartCallCount { get; private set; }
    public int MSearchCallCount { get; private set; }
    public int DisposeCallCount { get; private set; }

    /// <summary>When &gt; 0, <see cref="DisposeAsync"/> delays this long (FR-050 budget-overrun path).</summary>
    public TimeSpan TeardownDelay { get; set; } = TimeSpan.Zero;

    /// <summary>When true, <see cref="StartAsync"/> throws — simulates a new adapter failing to bind
    /// its sockets (AC-5.2.8 / D1 mid-rebuild failure).</summary>
    public bool FailOnStart { get; set; }

    public ChannelReader<SsdpDatagram> IncomingDatagrams => _channel.Reader;

    public ValueTask WriteAsync(SsdpDatagram d, CancellationToken ct = default) => _channel.Writer.WriteAsync(d, ct);

    public Task StartAsync(IPAddress adapterIPv4, CancellationToken ct)
    {
        StartCallCount++;
        StartedWith = adapterIPv4;
        _rec.Record($"transport[{_tag}].Start({adapterIPv4})");
        if (FailOnStart)
        {
            throw new System.IO.IOException($"transport[{_tag}] failed to bind {adapterIPv4} (test)");
        }

        return Task.CompletedTask;
    }

    public Task SendMSearchAsync(TimeSpan mx, CancellationToken ct)
    {
        MSearchCallCount++;
        _rec.Record($"transport[{_tag}].MSearch");
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        DisposeCallCount++;
        _rec.Record($"transport[{_tag}].Dispose");
        _channel.Writer.TryComplete();
        if (TeardownDelay > TimeSpan.Zero)
        {
            try { await Task.Delay(TeardownDelay).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { /* test settle */ }
        }
    }
}

/// <summary>
/// <see cref="IEventCallbackHost"/> fake that records start/dispose into a shared <see cref="SwitchRecorder"/>.
/// </summary>
internal sealed class RecordingCallbackHost : IEventCallbackHost
{
    private readonly SwitchRecorder _rec;
    private readonly string _tag;

    public RecordingCallbackHost(SwitchRecorder rec, string tag)
    {
        _rec = rec;
        _tag = tag;
    }

    public Uri CallbackBaseUrl { get; private set; } = new("http://127.0.0.1:0/");
    public int StartCallCount { get; private set; }
    public int DisposeCallCount { get; private set; }

    public event Func<NotifyRequest, Task>? NotifyReceived;

    public Task StartAsync(IPAddress adapterIPv4, CancellationToken ct)
    {
        StartCallCount++;
        CallbackBaseUrl = new Uri($"http://{adapterIPv4}:55555/");
        _rec.Record($"host[{_tag}].Start({adapterIPv4})");
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        DisposeCallCount++;
        _rec.Record($"host[{_tag}].Dispose");
        return ValueTask.CompletedTask;
    }

    // Keep NotifyReceived referenced so the compiler stays quiet (the real client subscribes to it).
    internal bool HasSubscriber => NotifyReceived is not null;
}

/// <summary>
/// Controllable <see cref="INetworkAdapterEnumerator"/>. Seeded with an initial adapter list; a test can
/// MUTATE it via <see cref="SetAdapters"/> between calls so the Story 2.12 (FR-057) network-change tests
/// can flip the eligible set (A → [B], A → []) to simulate a host network move and assert the re-enumerate.
/// </summary>
internal sealed class StubAdapterEnumerator : INetworkAdapterEnumerator
{
    private volatile NetworkAdapter[] _adapters;

    public StubAdapterEnumerator(params NetworkAdapter[] adapters) => _adapters = adapters;

    public IReadOnlyList<NetworkAdapter> Enumerate() => _adapters;

    /// <summary>Replace the eligible-adapter list returned by subsequent <see cref="Enumerate"/> calls.</summary>
    public void SetAdapters(params NetworkAdapter[] adapters) => _adapters = adapters;

    public static NetworkAdapter Adapter(string name, string ipv4) =>
        new(name, $"{name} (test)", IPAddress.Parse(ipv4));
}
