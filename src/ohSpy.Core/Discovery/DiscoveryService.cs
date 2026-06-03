namespace ohSpy.Core.Discovery;

using ohSpy.Core.Devices;
using ohSpy.Core.Threading;

internal sealed class DiscoveryService(
    ISsdpTransport transport,
    DeviceRegistry registry,
    SsdpParser parser,
    IUiDispatcher ui) : IDiscoveryService
{
    public event Action<SsdpAnnouncement>? AnnouncementReceived;

    private Task? _readLoop;
    private int _started;

    public Task StartAsync(CancellationToken adapterToken, CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
            throw new InvalidOperationException("StartAsync already called");
        _readLoop = Task.Run(() => ReadLoopAsync(adapterToken, ct));
        return Task.CompletedTask;
    }

    /// <summary>Re-issues M-SEARCH and prunes non-responders (E5). Stub in Story 2.4.</summary>
    public Task RescanAsync(CancellationToken ct) =>
        transport.SendMSearchAsync(TimeSpan.FromSeconds(5), ct);

    public async ValueTask DisposeAsync()
    {
        if (_readLoop is not null)
        {
            // VSTHRD003 suppressed: _readLoop is our own background loop started in StartAsync;
            // awaiting it here ensures the loop exits cleanly before disposal completes.
#pragma warning disable VSTHRD003
            try { await _readLoop.ConfigureAwait(false); }
#pragma warning restore VSTHRD003
            catch { /* loop exits via cancellation or channel completion */ }
        }
    }

    private async Task ReadLoopAsync(CancellationToken adapterToken, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(adapterToken, ct);
        try
        {
            await foreach (var datagram in transport.IncomingDatagrams
                               .ReadAllAsync(linked.Token)
                               .ConfigureAwait(false))
            {
                var remoteStr = datagram.Remote.ToString();
                var announcement = parser.Parse(datagram.Payload, remoteStr);
                if (announcement is null) continue; // Warning already emitted by parser

                var capturedAdapterToken = adapterToken;
                var capturedArrival = datagram.ArrivalUtc;
                ui.Post(() => RouteOnUiThread(announcement, capturedArrival, capturedAdapterToken));
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown — adapterToken or ct cancelled.
        }
    }

    private void RouteOnUiThread(SsdpAnnouncement ann, DateTime arrivalUtc, CancellationToken adapterToken)
    {
        // For M-SEARCH responses, ST plays the role of NT; NTS is absent (treat as ssdp:alive).
        var effectiveNt = ann.NT ?? ann.ST;

        if (ann.NTS?.Equals("ssdp:byebye", StringComparison.OrdinalIgnoreCase) == true)
        {
            if (ann.Uuid.HasValue &&
                effectiveNt?.Equals("upnp:rootdevice", StringComparison.OrdinalIgnoreCase) == true)
            {
                registry.OnByebye(ann.Uuid.Value); // FR-008
            }
        }
        else // ssdp:alive or M-SEARCH response (NTS absent)
        {
            if (ann.Uuid.HasValue && ann.Location is not null &&
                effectiveNt?.Equals("upnp:rootdevice", StringComparison.OrdinalIgnoreCase) == true)
            {
                registry.OnAlive(ann.Uuid.Value, ann.Location, arrivalUtc,
                    ann.Server, ann.CacheControlMaxAge, ann.BootId, ann.ConfigId,
                    adapterToken); // FR-005 / FR-007 / FR-043
            }
            // Non-root alives: registry untouched (FR-053 layer b)
        }

        // Raise for ALL successfully-parsed announcements (FR-014/FR-015 — log gets everything).
        AnnouncementReceived?.Invoke(ann);
    }
}
