namespace ohSpy.Core.Discovery;

using System.Threading.Channels;
using ohSpy.Core.Devices;
using ohSpy.Core.Models;
using ohSpy.Core.Threading;

/// <summary>
/// Reads the active <see cref="ISsdpTransport"/>'s datagram stream and routes announcements
/// into the registry + the SSDP log. Singleton for its lifetime (so <c>SsdpLogViewModel</c>'s
/// <see cref="AnnouncementReceived"/> subscription stays valid across adapter switches), but
/// its read loop is REBINDABLE: Amendment A23 (Story 5.2) decouples the service from any one
/// transport — it reads the <see cref="ChannelReader{SsdpDatagram}"/> handed to
/// <see cref="StartAsync"/> / <see cref="RebindAsync"/> (the scope-owned reader), so an
/// adapter switch points it at the fresh transport's reader without re-subscribing the log.
/// </summary>
internal sealed class DiscoveryService(
    DeviceRegistry registry,
    SsdpParser parser,
    IUiDispatcher ui) : IDiscoveryService
{
    public event Action<SsdpAnnouncement>? AnnouncementReceived;

    private Task? _readLoop;
    private int _started;

    public Task StartAsync(ChannelReader<SsdpDatagram> reader, CancellationToken adapterToken, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (Interlocked.Exchange(ref _started, 1) == 1)
            throw new InvalidOperationException("StartAsync already called");
        _readLoop = Task.Run(() => ReadLoopAsync(reader, adapterToken, ct));
        return Task.CompletedTask;
    }

    /// <summary>
    /// A23 / Story 5.2 atomic rebind: drains the read loop bound to the OLD (now-disposed)
    /// transport, resets the start guard, and starts a fresh loop against the NEW scope's
    /// <paramref name="reader"/> + adapter token. The old reader has already been completed by
    /// the old transport's <see cref="ISsdpTransport.DisposeAsync"/>, so the old loop's
    /// <c>ReadAllAsync</c> has finished; the await is the deliberate drain join.
    /// </summary>
    public async Task RebindAsync(ChannelReader<SsdpDatagram> reader, CancellationToken adapterToken, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reader);

        // Drain the previous loop (its reader was completed/cancelled by the old scope's teardown).
        // VSTHRD003 suppressed: _readLoop is our own background loop, awaited here as the rebind join.
        if (_readLoop is not null)
        {
#pragma warning disable VSTHRD003
            try { await _readLoop.ConfigureAwait(false); }
#pragma warning restore VSTHRD003
            catch { /* loop exits via cancellation or channel completion */ }
        }

        // Reset the single-start guard and bind the fresh reader. StartAsync only kicks the background
        // Task.Run loop and returns a completed task — awaiting it is the synchronous start join.
        Interlocked.Exchange(ref _started, 0);
        await StartAsync(reader, adapterToken, ct).ConfigureAwait(false);
    }

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

    private async Task ReadLoopAsync(ChannelReader<SsdpDatagram> reader, CancellationToken adapterToken, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(adapterToken, ct);
        try
        {
            await foreach (var datagram in reader
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
            if (!string.IsNullOrEmpty(ann.Udn) &&
                effectiveNt?.Equals("upnp:rootdevice", StringComparison.OrdinalIgnoreCase) == true)
            {
                registry.OnByebye(ann.Udn); // FR-008
            }
        }
        else // ssdp:alive or M-SEARCH response (NTS absent)
        {
            if (!string.IsNullOrEmpty(ann.Udn) && ann.Location is not null &&
                effectiveNt?.Equals("upnp:rootdevice", StringComparison.OrdinalIgnoreCase) == true)
            {
                registry.OnAlive(ann.Udn, ann.Location, arrivalUtc,
                    ann.Server, ann.CacheControlMaxAge, ann.BootId, ann.ConfigId,
                    adapterToken); // FR-005 / FR-007 / FR-043
            }
            // Non-root alives: registry untouched (FR-053 layer b)
        }

        // Raise for ALL successfully-parsed announcements (FR-014/FR-015 — log gets everything).
        AnnouncementReceived?.Invoke(ann);
    }
}
