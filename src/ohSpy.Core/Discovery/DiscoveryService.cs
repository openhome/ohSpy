namespace ohSpy.Core.Discovery;

using System.Threading.Channels;
using ohSpy.Core.Devices;
using ohSpy.Core.Diagnostics;
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
/// <para>
/// FR-056 / Amendment A33 (Story 2.11): alongside the read loop it runs a periodic <b>expiry sweep</b>
/// (<see cref="SweepLoopAsync"/>) — the automatic per-entry-lease cousin of the Story 5.3 manual
/// <c>PruneNotSeenSince</c>. The sweep evicts a device whose <c>CACHE-CONTROL: max-age</c> lease has
/// lapsed without a refreshing alive (inferred byebye, UDA 1.0 §1.2.2), reusing the registry's
/// byebye-identical <c>RemoveCore</c> cascade. The eviction is marshalled onto the UI thread via
/// <see cref="IUiDispatcher.Post"/> (the registry is UI-thread-owned), and each eviction emits a
/// <see cref="DiagCategories.SsdpExpired"/> diagnostic. The sweep hangs off the SAME adapter-scoped
/// lifecycle as the read loop (bound to the same linked token, drained in <see cref="RebindAsync"/> /
/// <see cref="DisposeAsync"/>), so it stops cleanly on adapter switch / teardown.
/// </para>
/// </summary>
internal sealed class DiscoveryService(
    DeviceRegistry registry,
    SsdpParser parser,
    IUiDispatcher ui,
    IDiagnosticEmitter diagnostics) : IDiscoveryService
{
    /// <summary>FR-056 default lease when an alive omitted <c>CACHE-CONTROL: max-age</c> (UDA 1.0 §1.2.2 example).</summary>
    private static readonly TimeSpan DefaultLease = TimeSpan.FromSeconds(1800);

    /// <summary>FR-056 grace — a small fixed jitter tolerance added to the lease (routing latency + clock skew).</summary>
    private static readonly TimeSpan ExpiryJitter = TimeSpan.FromSeconds(5);

    public event Action<SsdpAnnouncement>? AnnouncementReceived;

    private Task? _readLoop;
    private Task? _sweepLoop;
    private CancellationTokenSource? _sweepCts;
    private int _started;

    // ── Test seams (InternalsVisibleTo; the SubscriptionClient._delay / ShellViewModel._rescanDelay
    //    precedent). Defaulted so production is unchanged; tests swap them so the sweep runs instantly
    //    with a controlled "now" — NO real multi-minute waits (AC #8). ──
    private Func<DateTime> _clock = () => DateTime.UtcNow;
    private Func<TimeSpan, CancellationToken, Task> _delay = (d, ct) => Task.Delay(d, ct);
    private TimeSpan _sweepInterval = TimeSpan.FromSeconds(30);

    /// <summary>Test seam: replace the expiry-sweep clock (the "now" each sweep evaluates leases against).</summary>
    internal void SetClockForTest(Func<DateTime> clock) => _clock = clock;

    /// <summary>Test seam: replace the sweep-loop delay (no real 30 s sleep — drive the loop instantly).</summary>
    internal void SetSweepDelayForTest(Func<TimeSpan, CancellationToken, Task> delay) => _delay = delay;

    /// <summary>Test seam: shrink the sweep interval (the delay seam usually makes this moot, but kept for symmetry).</summary>
    internal void SetSweepIntervalForTest(TimeSpan interval) => _sweepInterval = interval;

    public Task StartAsync(ChannelReader<SsdpDatagram> reader, CancellationToken adapterToken, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (Interlocked.Exchange(ref _started, 1) == 1)
            throw new InvalidOperationException("StartAsync already called");
        _readLoop = Task.Run(() => ReadLoopAsync(reader, adapterToken, ct));
        // The sweep loop has no channel-completion stop signal like the read loop, so the service owns a
        // CTS that RebindAsync / DisposeAsync cancel to drain it (alongside the adapter token / ct).
        _sweepCts = new CancellationTokenSource();
        var sweepStopToken = _sweepCts.Token;
        _sweepLoop = Task.Run(() => SweepLoopAsync(adapterToken, ct, sweepStopToken));
        return Task.CompletedTask;
    }

    private void StopSweep()
    {
        if (_sweepCts is not null)
        {
            _sweepCts.Cancel();
            _sweepCts.Dispose();
            _sweepCts = null;
        }
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

        // Drain the previous loops (the read loop's reader was completed/cancelled by the old scope's
        // teardown; the sweep loop's linked token cancels when the old adapter token cancels).
        // VSTHRD003 suppressed: these are our own background loops, awaited here as the rebind join.
        if (_readLoop is not null)
        {
#pragma warning disable VSTHRD003
            try { await _readLoop.ConfigureAwait(false); }
#pragma warning restore VSTHRD003
            catch { /* loop exits via cancellation or channel completion */ }
        }
        StopSweep(); // signal the sweep loop to exit (it has no channel-completion stop of its own)
        if (_sweepLoop is not null)
        {
#pragma warning disable VSTHRD003
            try { await _sweepLoop.ConfigureAwait(false); }
#pragma warning restore VSTHRD003
            catch { /* sweep exits via cancellation (adapter switch / teardown) */ }
        }

        // Reset the single-start guard and bind the fresh reader. StartAsync only kicks the background
        // Task.Run loops and returns a completed task — awaiting it is the synchronous start join.
        Interlocked.Exchange(ref _started, 0);
        await StartAsync(reader, adapterToken, ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        // VSTHRD003 suppressed: these are our own background loops started in StartAsync;
        // awaiting them here ensures both exit cleanly before disposal completes.
        if (_readLoop is not null)
        {
#pragma warning disable VSTHRD003
            try { await _readLoop.ConfigureAwait(false); }
#pragma warning restore VSTHRD003
            catch { /* loop exits via cancellation or channel completion */ }
        }
        StopSweep(); // signal the sweep loop to exit before awaiting its drain
        if (_sweepLoop is not null)
        {
#pragma warning disable VSTHRD003
            try { await _sweepLoop.ConfigureAwait(false); }
#pragma warning restore VSTHRD003
            catch { /* sweep exits via cancellation (adapter switch / teardown) */ }
        }
    }

    /// <summary>
    /// FR-056 / Amendment A33 periodic expiry sweep. Wakes every <see cref="_sweepInterval"/>, reads
    /// "now" off-thread, then marshals <see cref="DeviceRegistry.ExpireOlderThan"/> onto the UI thread
    /// (the registry is UI-thread-owned). Each eviction emits a <see cref="DiagCategories.SsdpExpired"/>
    /// diagnostic. Bound to the SAME <paramref name="adapterToken"/>/<paramref name="ct"/> linked token
    /// as the read loop, so it stops on adapter switch / teardown and never mutates a torn-down or
    /// replaced registry from a stale timer thread (AC #7). Never blocks the read loop (separate Task).
    /// </summary>
    private async Task SweepLoopAsync(CancellationToken adapterToken, CancellationToken ct, CancellationToken stopToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(adapterToken, ct, stopToken);
        try
        {
            while (!linked.IsCancellationRequested)
            {
                await _delay(_sweepInterval, linked.Token).ConfigureAwait(false);
                var now = _clock();
                ui.Post(() =>
                {
                    // ExpireOlderThan does the byebye-identical eviction (cancel + dispose DeviceCts,
                    // raise DeviceRemoved → FR-037 popups + AC-7.2 fetch-cancel). It returns each evicted
                    // device with the max-age it advertised so we emit one PER-DEVICE-ACCURATE SsdpExpired
                    // — the registry deliberately has no IDiagnosticEmitter dependency (DI-cycle), so
                    // DiscoveryService owns the emit.
                    var evicted = registry.ExpireOlderThan(now, DefaultLease, ExpiryJitter);
                    foreach (var (udn, maxAge) in evicted)
                    {
                        var reason = maxAge is { } ma
                            ? $"no ssdp:alive within its {(int)ma.TotalSeconds}s CACHE-CONTROL max-age lease + {(int)ExpiryJitter.TotalSeconds}s grace"
                            : $"no ssdp:alive within the {(int)DefaultLease.TotalSeconds}s default lease (no CACHE-CONTROL advertised) + {(int)ExpiryJitter.TotalSeconds}s grace";
                        diagnostics.Information(
                            DiagCategories.SsdpExpired,
                            "Device evicted: no ssdp:alive within its CACHE-CONTROL lease (inferred byebye).",
                            new DiagnosticContext { DeviceUuid = udn, ErrorText = reason });
                    }
                });
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown — adapter switch / teardown cancelled the linked token.
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
