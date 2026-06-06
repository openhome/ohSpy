namespace ohSpy.Soak.Tests.Farm;

using System.Globalization;

/// <summary>
/// Story 6.2 — the FakeUpnpDevice farm orchestrator (Task 2). Stands up N farm devices, drives the
/// SSDP advertiser loop (configurable adv/s) writing per-device <c>NOTIFY ssdp:alive</c> into the
/// scope-owned <see cref="FarmSsdpTransport"/>, supports byebye-on-demand for the
/// mid-interaction-disappear device, and exposes the device list + the misbehaving subset so the
/// harness can wire the GENA event-emitter and the cold-expand timeout step.
/// <para>All test-scoped; never promoted to production.</para>
/// </summary>
internal sealed class DeviceFarm : IAsyncDisposable
{
    private readonly List<FarmUpnpDevice> _devices = new();
    private readonly FarmSsdpTransport _transport;
    private readonly TimeSpan _advInterval;
    private readonly CancellationTokenSource _cts = new();
    private Task? _advertiserLoop;

    public DeviceFarm(FarmSsdpTransport transport, int advertsPerSecond)
    {
        _transport = transport;
        // Burst-capable: advertsPerSecond is the SUSTAINED rate across the whole farm; >= 20/s is the
        // burst target (6.1.14 / 6.3 chatty-SSDP). Clamp to a sane floor so the channel always churns.
        var perSec = Math.Max(1, advertsPerSecond);
        _advInterval = TimeSpan.FromMilliseconds(1000.0 / perSec);
    }

    public IReadOnlyList<FarmUpnpDevice> Devices => _devices;

    /// <summary>Devices flagged for live NOTIFY traffic (the subscription-popup targets).</summary>
    public List<FarmUpnpDevice> EventEmitters { get; } = new();

    /// <summary>Stand up <paramref name="normalCount"/> normal devices + the misbehaving set
    /// (slow responder, GiantScpd) and bind their HTTP endpoints. Returns once all are listening.</summary>
    public async Task BuildAsync(int normalCount, bool includeMisbehaving, CancellationToken ct)
    {
        for (var i = 0; i < normalCount; i++)
        {
            await AddDeviceAsync(new DeviceSpec(
                UdnBody: $"soak-normal-{i.ToString(CultureInfo.InvariantCulture)}-{Guid.NewGuid():N}",
                FriendlyName: $"Soak Normal {i}",
                Behavior: DeviceBehavior.Normal), ct).ConfigureAwait(false);
        }

        if (includeMisbehaving)
        {
            SlowResponder = await AddDeviceAsync(new DeviceSpec(
                UdnBody: $"soak-slow-{Guid.NewGuid():N}",
                FriendlyName: "Soak Slow Responder",
                Behavior: DeviceBehavior.SlowResponder), ct).ConfigureAwait(false);

            GiantScpdDevice = await AddDeviceAsync(new DeviceSpec(
                UdnBody: $"soak-giant-{Guid.NewGuid():N}",
                FriendlyName: "Soak Giant SCPD",
                Behavior: DeviceBehavior.GiantScpd), ct).ConfigureAwait(false);

            // The mid-interaction-byebye device + the partial-NOTIFY device are normal HTTP devices;
            // their misbehaviour is in the datagram / event stream, not an HTTP mode.
            ByebyeDevice = await AddDeviceAsync(new DeviceSpec(
                UdnBody: $"soak-byebye-{Guid.NewGuid():N}",
                FriendlyName: "Soak Byebye Device",
                Behavior: DeviceBehavior.Normal), ct).ConfigureAwait(false);

            PartialNotifyDevice = await AddDeviceAsync(new DeviceSpec(
                UdnBody: $"soak-partial-{Guid.NewGuid():N}",
                FriendlyName: "Soak Partial NOTIFY Device",
                Behavior: DeviceBehavior.Normal), ct).ConfigureAwait(false);
        }
    }

    public FarmUpnpDevice? SlowResponder { get; private set; }
    public FarmUpnpDevice? GiantScpdDevice { get; private set; }
    public FarmUpnpDevice? ByebyeDevice { get; private set; }
    public FarmUpnpDevice? PartialNotifyDevice { get; private set; }

    private async Task<FarmUpnpDevice> AddDeviceAsync(DeviceSpec spec, CancellationToken ct)
    {
        var device = new FarmUpnpDevice(spec);
        await device.StartAsync(ct).ConfigureAwait(false);
        _devices.Add(device);
        return device;
    }

    /// <summary>Start the background SSDP advertiser loop. Writes one device's alive per tick,
    /// round-robin, at the configured rate — so over the run the SSDP log sits at/near saturation.</summary>
    public void StartAdvertiser()
    {
        _advertiserLoop = Task.Run(() => AdvertiseLoopAsync(_cts.Token));
        // Re-burst on every M-SEARCH (startup + rescan), exactly as real devices answer a search.
        _transport.MSearchIssued += OnMSearch;
    }

    private void OnMSearch()
    {
        // Fire a quick alive burst for every live device so a rescan/startup populates the tree fast.
        _ = BurstAliveAsync(_cts.Token);
    }

    /// <summary>Emit one alive for every device immediately (the search-response burst).</summary>
    public async Task BurstAliveAsync(CancellationToken ct)
    {
        foreach (var device in _devices.ToArray())
        {
            if (ct.IsCancellationRequested)
            {
                return;
            }
            try
            {
                await _transport.WriteAsync(
                    SoakSsdpDatagram.Alive(device.UdnBody, device.DescriptionUrl.ToString()), ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { /* channel closing — tolerated */ }
        }
    }

    private async Task AdvertiseLoopAsync(CancellationToken ct)
    {
        var idx = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var snapshot = _devices.ToArray();
                if (snapshot.Length > 0)
                {
                    var device = snapshot[idx % snapshot.Length];
                    idx++;
                    await _transport.WriteAsync(
                        SoakSsdpDatagram.Alive(device.UdnBody, device.DescriptionUrl.ToString()), ct)
                        .ConfigureAwait(false);
                }
                await Task.Delay(_advInterval, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Channel completed mid-write during teardown — tolerated.
        }
    }

    /// <summary>Send a byebye for the mid-interaction-disappear device (the registry drops the row +
    /// cascades FR-037 to any open popup). Idempotent / best-effort.</summary>
    public async Task SendByebyeAsync(FarmUpnpDevice device, CancellationToken ct)
    {
        try
        {
            await _transport.WriteAsync(
                SoakSsdpDatagram.Byebye(device.UdnBody, device.DescriptionUrl.ToString()), ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { /* tolerated */ }
    }

    public async ValueTask DisposeAsync()
    {
        _transport.MSearchIssued -= OnMSearch;
        try { await _cts.CancelAsync().ConfigureAwait(false); } catch (ObjectDisposedException) { }
        if (_advertiserLoop is not null)
        {
            try { await _advertiserLoop.ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { /* loop swallows its own */ }
        }
        foreach (var device in _devices)
        {
            await device.DisposeAsync().ConfigureAwait(false);
        }
        _cts.Dispose();
    }
}
