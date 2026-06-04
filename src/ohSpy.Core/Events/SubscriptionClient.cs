namespace ohSpy.Core.Events;

using System.Collections.Concurrent;
using System.Threading.Channels;
using System.Xml;
using ohSpy.Core.Devices;
using ohSpy.Core.Diagnostics;
using ohSpy.Core.Http;
using ohSpy.Core.Models;
using ohSpy.Core.Scpd;

/// <summary>
/// The GENA subscription lifecycle orchestrator (Story 4.2). It ORCHESTRATES the shipped Story 1.3
/// verbs (<see cref="IUpnpHttpClient.SubscribeAsync"/> / <see cref="IUpnpHttpClient.RenewSubscriptionAsync"/>
/// / <see cref="IUpnpHttpClient.UnsubscribeAsync"/>) — it does NOT rebuild SUBSCRIBE — and consumes the
/// Story 4.1 callback host (<see cref="IEventCallbackHost.CallbackBaseUrl"/> + <see cref="IEventCallbackHost.NotifyReceived"/>).
/// Headless Core (no UI, no bound VM state). <c>internal sealed</c> per Pattern 7 — DI registers
/// <see cref="ISubscriptionClient"/>.
/// <para>
/// CORRELATION: inbound NOTIFYs route by <see cref="NotifyRequest.Sid"/> through a thread-safe map
/// (NOT a callback-path token in v1 — single shared <c>CallbackBaseUrl</c>, SID is the canonical GENA
/// discriminator). The NOTIFY-before-SID race (a device can fire NOTIFY #0 before our SUBSCRIBE
/// response returns the SID) is closed by a per-subscribe pending buffer drained at SID registration.
/// PARSE BOUNDARY: 4.2 parses <c>&lt;e:propertyset&gt;</c> with the shared XXE-locked
/// <see cref="UpnpXmlReaderSettings"/>; 4.3 only renders. NON-SERIAL: each subscription owns a bounded
/// channel + drain worker so a slow parse on one subscription never blocks another nor the host's
/// awaited handler (AC-4.2.9).
/// </para>
/// </summary>
internal sealed class SubscriptionClient : ISubscriptionClient
{
    // ── 4.2 constants (NOT in HttpTimeoutOptions — those are per-request budgets) ──
    /// <summary>Initial requested lease (Open Q1). 300 s is the conventional UPnP default and well
    /// within UDA norms; the device may grant less and we renew off the GRANTED value.</summary>
    private static readonly TimeSpan InitialLease = TimeSpan.FromSeconds(300);

    /// <summary>Per-renew request budget passed to RenewSubscriptionAsync (same 300 s requested lease).</summary>
    private static readonly TimeSpan RenewRequestedLease = TimeSpan.FromSeconds(300);

    /// <summary>Margin floor — never schedule a renew sooner than this (guards a pathological tiny lease).</summary>
    private static readonly TimeSpan MinRenewDelay = TimeSpan.FromSeconds(1);

    /// <summary>UNSUBSCRIBE-on-close budget (D7). A hung device must not block popup close.</summary>
    private static readonly TimeSpan UnsubscribeBudget = TimeSpan.FromSeconds(5);

    /// <summary>Bounded wait for the renew loop + NOTIFY worker to drain during close, so no event or
    /// lapse is raised AFTER <c>CloseAsync</c> returns (the 4.3 popup detaches its handler on close; a
    /// post-close marshalled NOTIFY would race that teardown). A hung parse/renew can't block close.</summary>
    private static readonly TimeSpan DrainBudget = TimeSpan.FromSeconds(2);

    /// <summary>Bounded NOTIFY queue depth per subscription (FR-104 — bounded, no device back-pressure).</summary>
    private const int NotifyQueueCapacity = 256;

    /// <summary>
    /// Renew margin (Open Q1). Renew at ~80% of the granted lease, but never later than
    /// <c>granted − 30 s</c> (so a short lease still renews with a sane head start), clamped to a
    /// <see cref="MinRenewDelay"/> floor.
    /// </summary>
    private static TimeSpan RenewDelayFor(TimeSpan granted)
    {
        var eighty = granted * 0.8;
        var minusThirty = granted - TimeSpan.FromSeconds(30);
        var chosen = minusThirty < eighty ? minusThirty : eighty;
        return chosen < MinRenewDelay ? MinRenewDelay : chosen;
    }

    private readonly IUpnpHttpClient _http;
    private readonly IEventCallbackHost _callbackHost;
    private readonly IDiagnosticEmitter _diag;

    // The injectable delay seam (Open Q2 / AC-4.2.16) — defaults to Task.Delay so the renew loop is
    // testable WITHOUT real waits (mirrors EventCallbackHost's internal-test-ctor budget seam).
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    // SID → live subscription (AC-4.2.6). Thread-safe: the host raises NotifyReceived from its
    // handler tasks and renew loops run on background tasks (AC-4.2.17).
    private readonly ConcurrentDictionary<string, Subscription> _bySid = new();

    // Pending buffers keyed by a per-subscribe correlation id, for the NOTIFY-before-SID race
    // (AC-4.2.7). A NOTIFY whose SID matches no live subscription while a SUBSCRIBE is in flight is
    // buffered here and replayed when that subscription registers its SID.
    private readonly ConcurrentDictionary<Guid, Subscription> _pending = new();

    private CancellationToken _adapterToken = CancellationToken.None;
    private int _subscribedToHost;

    public SubscriptionClient(IUpnpHttpClient http, IEventCallbackHost callbackHost, IDiagnosticEmitter diag)
        : this(http, callbackHost, diag, static (d, ct) => Task.Delay(d, ct))
    {
    }

    /// <summary>Test seam: injectable renew-delay so auto-renew timing is testable without real waits
    /// (AC-4.2.16 — the EventCallbackHost internal-test-ctor precedent).</summary>
    internal SubscriptionClient(
        IUpnpHttpClient http,
        IEventCallbackHost callbackHost,
        IDiagnosticEmitter diag,
        Func<TimeSpan, CancellationToken, Task> delay)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(callbackHost);
        ArgumentNullException.ThrowIfNull(diag);
        ArgumentNullException.ThrowIfNull(delay);
        _http = http;
        _callbackHost = callbackHost;
        _diag = diag;
        _delay = delay;

        // Subscribe ONCE to the host for the client's lifetime (one host, one client).
        if (Interlocked.Exchange(ref _subscribedToHost, 1) == 0)
        {
            _callbackHost.NotifyReceived += OnNotifyReceivedAsync;
        }
    }

    public void SetAdapterContext(CancellationToken adapterToken) => _adapterToken = adapterToken;

    public async Task<SubscriptionHandle> SubscribeAsync(
        ServiceDescription service, RegistryEntry parentEntry, CancellationToken popupToken)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(parentEntry);

        // Resolve the absolute eventSubURL (EventSubUrl is a possibly-RELATIVE string — the
        // InvocationPopupViewModel control-URL precedent). Malformed → fail like a transport error,
        // NO SID, NO UNSUBSCRIBE (AC-4.2.5 / AC-4.2.10).
        if (!Uri.TryCreate(parentEntry.LocationUrl, service.EventSubUrl, out var eventSubUrl))
        {
            _diag.Warning(DiagCategories.GenaSubscribeFailed, "SUBSCRIBE failed — malformed eventSubURL",
                new DiagnosticContext
                {
                    DeviceUuid = parentEntry.Uuid,
                    Url = $"{parentEntry.LocationUrl} + {service.EventSubUrl}",
                    ErrorText = "could not resolve an absolute eventSubURL",
                });
            throw new UpnpProtocolException(parentEntry.LocationUrl, "malformed eventSubURL");
        }

        // Register the pending buffer BEFORE awaiting SubscribeAsync so a NOTIFY landing in the gap is
        // captured and replayed at SID registration (AC-4.2.7).
        var sub = new Subscription(this, eventSubUrl, parentEntry.Uuid, parentEntry.DeviceToken, popupToken);
        _pending[sub.PendingId] = sub;

        SubscribeResponse response;
        try
        {
            response = await _http.SubscribeAsync(eventSubUrl, _callbackHost.CallbackBaseUrl, InitialLease, popupToken)
                                  .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is UpnpException)
        {
            // Failed SUBSCRIBE: register NOTHING, never UNSUBSCRIBE (no SID). Emit the UUID-bearing
            // Warning atop the verb-layer's uuid-less HttpTransport/HttpTimeout (intentional-duplicate
            // pattern, Story 3.2). Rethrow so the caller observes it (AC-4.2.10).
            _pending.TryRemove(sub.PendingId, out _);
            sub.DisposeResources();
            _diag.Warning(DiagCategories.GenaSubscribeFailed, "SUBSCRIBE failed",
                new DiagnosticContext
                {
                    DeviceUuid = parentEntry.Uuid,
                    Url = eventSubUrl.ToString(),
                    ErrorText = ex.Message,
                });
            throw;
        }

        // Register SID → subscription, then drain anything the race buffered. The map write makes the
        // subscription visible to OnNotifyReceivedAsync before we drain (AC-4.2.6/4.2.7).
        sub.Activate(response.Sid, response.Timeout);
        _bySid[response.Sid] = sub;
        _pending.TryRemove(sub.PendingId, out _);
        sub.DrainPendingBuffer();

        sub.StartRenewLoop();
        sub.StartNotifyWorker();

        _diag.Verbose(DiagCategories.GenaSubscribe, "GENA SUBSCRIBE granted",
            new DiagnosticContext { DeviceUuid = parentEntry.Uuid, Url = eventSubUrl.ToString(), Sid = response.Sid });

        return sub.Handle;
    }

    // The single NotifyReceived handler (registered once in the ctor). MUST return promptly — the host
    // AWAITS it (AC-4.2.9). It only routes + enqueues; parsing happens on the subscription's worker.
    private Task OnNotifyReceivedAsync(NotifyRequest req)
    {
        if (!string.IsNullOrEmpty(req.Sid) && _bySid.TryGetValue(req.Sid, out var sub))
        {
            sub.Enqueue(req);
            return Task.CompletedTask;
        }

        // No live SID match. If a SUBSCRIBE is in flight, buffer for the race (AC-4.2.7); else drop
        // silently — the host already returned its idempotent 200 (D4 L444).
        foreach (var pending in _pending.Values)
        {
            // The pending subscription has no SID yet; buffer every in-flight subscribe's first NOTIFYs.
            // At SID registration only the matching SID's events are replayed (others stay buffered and
            // are discarded with the buffer if the subscribe fails — a dropped pre-SID NOTIFY for a
            // never-created subscription is correct).
            pending.BufferPending(req);
        }

        return Task.CompletedTask;
    }

    // ── Per-subscription state ──────────────────────────────────────────────────
    private sealed class Subscription
    {
        private readonly SubscriptionClient _owner;
        private readonly Uri _eventSubUrl;
        private readonly Guid _deviceUuid;
        private readonly CancellationToken _deviceToken;
        private readonly CancellationToken _popupToken;

        // Renew-loop token: linked across popup + device + adapter so popup-close, device-gone and
        // adapter-switch all abort it (D7 — AC-4.2.15). Created at Activate (the adapter token is known
        // by then; SetAdapterContext runs at startup, before any SubscribeAsync).
        private CancellationTokenSource? _loopCts;

        private readonly Channel<NotifyRequest> _channel = Channel.CreateBounded<NotifyRequest>(
            new BoundedChannelOptions(NotifyQueueCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest, // FIFO tail-eviction, no device back-pressure
                SingleReader = true,
                SingleWriter = false,
            });

        private readonly ConcurrentQueue<NotifyRequest> _pendingBuffer = new();

        private Task? _renewLoop;
        private Task? _notifyWorker;
        private TimeSpan _granted;
        private int _lapsed;   // 0 active, 1 lapsed (no UNSUBSCRIBE on close)
        private int _stopped;  // renew loop should stop (lapse or close)

        public Subscription(
            SubscriptionClient owner, Uri eventSubUrl, Guid deviceUuid,
            CancellationToken deviceToken, CancellationToken popupToken)
        {
            _owner = owner;
            _eventSubUrl = eventSubUrl;
            _deviceUuid = deviceUuid;
            _deviceToken = deviceToken;
            _popupToken = popupToken;
            PendingId = Guid.NewGuid();
            // Provisional handle; Sid is filled at Activate via re-construction is avoided — the handle
            // needs the SID, so it is created at Activate.
        }

        public Guid PendingId { get; }

        public SubscriptionHandle Handle { get; private set; } = null!;

        public string Sid { get; private set; } = string.Empty;

        public void Activate(string sid, TimeSpan granted)
        {
            Sid = sid;
            _granted = granted;
            Handle = new SubscriptionHandle(sid, CloseAsync);
        }

        public void BufferPending(NotifyRequest req) => _pendingBuffer.Enqueue(req);

        public void DrainPendingBuffer()
        {
            while (_pendingBuffer.TryDequeue(out var req))
            {
                // Only replay NOTIFYs whose SID matches the one we were granted (the buffer may have
                // captured another in-flight subscribe's event).
                if (string.Equals(req.Sid, Sid, StringComparison.Ordinal))
                {
                    Enqueue(req);
                }
            }
        }

        public void Enqueue(NotifyRequest req) => _channel.Writer.TryWrite(req);

        public void StartNotifyWorker()
        {
            // Pattern 6: Task.Run for a long-running drain loop (real async work inside) — same
            // justification + pragma as SsdpTransport/EventCallbackHost.
#pragma warning disable VSTHRD110
            _notifyWorker = Task.Run(NotifyWorkerAsync);
#pragma warning restore VSTHRD110
        }

        private async Task NotifyWorkerAsync()
        {
            try
            {
                await foreach (var req in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
                {
                    ParseAndRaise(req);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // The worker must never throw out — a residual fault is swallowed (the channel is the
                // only writer path; nothing observes this task).
            }
        }

        private void ParseAndRaise(NotifyRequest req)
        {
            Dictionary<string, string>? properties = TryParsePropertyset(req.Body);
            if (properties is null)
            {
                // Malformed propertyset → swallow that one NOTIFY (Open Q5). Do NOT lapse, do NOT crash.
                _owner._diag.Verbose(DiagCategories.GenaNotifyReceived, "dropped malformed propertyset",
                    new DiagnosticContext { DeviceUuid = _deviceUuid, Url = _eventSubUrl.ToString(), Sid = Sid });
                return;
            }

            var notification = new EventNotification(Sid, req.Seq, req.ReceivedUtc, properties);
            Handle.RaiseNotification(notification);
        }

        private static Dictionary<string, string>? TryParsePropertyset(byte[] body)
        {
            // <e:propertyset xmlns:e="urn:schemas-upnp-org:event-1-0">
            //   <e:property><VarName>value</VarName></e:property> …
            // </e:propertyset>
            // Extract each inner element name → text (the property name is the element directly inside
            // <e:property>). XXE-locked via the shared settings (Story 1.4 discipline).
            try
            {
                var properties = new Dictionary<string, string>(StringComparer.Ordinal);
                using var ms = new MemoryStream(body, writable: false);
                using var reader = XmlReader.Create(ms, UpnpXmlReaderSettings.Create());

                bool sawPropertyset = false;
                while (reader.Read())
                {
                    if (reader.NodeType != XmlNodeType.Element)
                    {
                        continue;
                    }

                    if (reader.LocalName == "propertyset")
                    {
                        sawPropertyset = true;
                        continue;
                    }

                    if (reader.LocalName == "property")
                    {
                        continue;
                    }

                    // Any element nested below <e:property> is a property variable. (The propertyset/
                    // property wrappers are skipped above; the next element depth is the var.)
                    var name = reader.LocalName;
                    var value = reader.ReadElementContentAsString();
                    properties[name] = value;
                }

                return sawPropertyset ? properties : null;
            }
            catch (Exception ex) when (ex is XmlException or InvalidOperationException or FormatException)
            {
                return null;
            }
        }

        private CancellationTokenRegistration _deviceReg;
        private CancellationTokenRegistration _adapterReg;

        public void StartRenewLoop()
        {
            _loopCts = CancellationTokenSource.CreateLinkedTokenSource(_popupToken, _deviceToken, _owner._adapterToken);

            // Lapse the subscription PROMPTLY on device-gone / adapter-switch via cancellation callbacks,
            // independent of the renew-loop task being scheduled (D7 — AC-4.2.15). The renew loop still
            // owns renew-FAILURE lapses; `Lapse` is idempotent (Interlocked guard) so whichever path fires
            // first wins and the other is a no-op. Device is checked first (device ⊂ adapter).
            _deviceReg = _deviceToken.Register(() => Lapse(SubscriptionLapseReason.DeviceGone));
            _adapterReg = _owner._adapterToken.CanBeCanceled
                ? _owner._adapterToken.Register(() => Lapse(SubscriptionLapseReason.AdapterSwitch))
                : default;

#pragma warning disable VSTHRD110
            _renewLoop = Task.Run(() => RenewLoopAsync(_loopCts.Token));
#pragma warning restore VSTHRD110
        }

        private async Task RenewLoopAsync(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested && Volatile.Read(ref _stopped) == 0)
                {
                    var delay = RenewDelayFor(_granted);
                    await _owner._delay(delay, token).ConfigureAwait(false);

                    if (token.IsCancellationRequested || Volatile.Read(ref _stopped) == 1)
                    {
                        return;
                    }

                    SubscribeResponse renewed;
                    try
                    {
                        renewed = await _owner._http
                            .RenewSubscriptionAsync(_eventSubUrl, Sid, RenewRequestedLease, token)
                            .ConfigureAwait(false);
                    }
                    catch (UpnpTransportException ex) when (ex.StatusCode == 412)
                    {
                        // RENEW refused (412) → stop, lapse, NO retry, NO unsubscribe (AC-4.2.12).
                        Lapse(SubscriptionLapseReason.RenewRefused);
                        EmitRenewFailed(ex.Message);
                        return;
                    }
                    catch (Exception ex) when (ex is UpnpException)
                    {
                        // Transport / timeout / protocol RENEW failure → stop, lapse, NO retry (AC-4.2.12).
                        Lapse(SubscriptionLapseReason.RenewTransportError);
                        EmitRenewFailed(ex.Message);
                        return;
                    }

                    // Success — the new granted lease replaces the prior and the loop reschedules.
                    _granted = renewed.Timeout;
                }
            }
            catch (OperationCanceledException)
            {
                // Cancellation is the normal path on shutdown (AC-4.2.18). Disambiguate the reason:
                // adapter switch vs device-gone vs popup-close (the last sends no lapse — an explicit
                // CloseAsync owns teardown).
                OnLoopCancelled();
            }
        }

        private void OnLoopCancelled()
        {
            // A popup-close (or explicit CloseAsync) sets _stopped; in that case CloseAsync owns the
            // teardown and we do NOT raise a lapse.
            if (Volatile.Read(ref _stopped) == 1)
            {
                return;
            }

            // Adapter switch and device-gone both cancel the loop token; report the right reason and
            // mark lapsed so a subsequent CloseAsync sends no UNSUBSCRIBE (AC-4.2.15). Distinguish by
            // which level-above token fired (device first, then adapter).
            if (_deviceToken.IsCancellationRequested)
            {
                Lapse(SubscriptionLapseReason.DeviceGone);
            }
            else if (_owner._adapterToken.IsCancellationRequested)
            {
                Lapse(SubscriptionLapseReason.AdapterSwitch);
            }
            else
            {
                // Popup token fired without _stopped being set yet (the CloseAsync ordering races the
                // loop). Treat as a clean stop — CloseAsync will run.
                Volatile.Write(ref _stopped, 1);
            }
        }

        private void Lapse(SubscriptionLapseReason reason)
        {
            // Mark lapsed + stopped BEFORE de-registering so a concurrent CloseAsync observes the lapse
            // and sends no UNSUBSCRIBE (AC-4.2.12/4.2.14).
            Volatile.Write(ref _stopped, 1);
            if (Interlocked.Exchange(ref _lapsed, 1) == 1)
            {
                return; // already lapsed — raise once
            }

            _owner._bySid.TryRemove(Sid, out _);
            Handle.RaiseLapsed(reason);
        }

        private void EmitRenewFailed(string error) =>
            _owner._diag.Warning(DiagCategories.GenaRenewFailed, "GENA RENEW failed — subscription lapsed",
                new DiagnosticContext { DeviceUuid = _deviceUuid, Url = _eventSubUrl.ToString(), Sid = Sid, ErrorText = error });

        public async Task CloseAsync()
        {
            // Stop the renew loop / signal teardown FIRST so the loop won't fire a stray lapse.
            Volatile.Write(ref _stopped, 1);

            // Cancel the renew loop's token (popup-derived internal state). NOTE: we do NOT use this
            // token for the UNSUBSCRIBE below (D7 level-above invariant).
            if (_loopCts is not null)
            {
                try { await _loopCts.CancelAsync().ConfigureAwait(false); }
                catch (ObjectDisposedException) { /* teardown race tolerated */ }
            }

            // Stop the NOTIFY worker (complete the channel; the drain loop exits when emptied).
            _channel.Writer.TryComplete();

            // Patch 2: await the renew loop + NOTIFY worker (bounded) so neither raises an event/lapse
            // AFTER this method returns. The 4.3 popup detaches its handler on close; a post-close
            // RaiseNotification is _ui.Post-marshalled and would race the VM teardown (the 3.2 crash
            // class). Awaiting the renew loop first also means any renew-failure Lapse has completed
            // before we read _lapsed below (tightens the Patch-1 window to near-zero).
#pragma warning disable VSTHRD003 // our own background tasks, awaited only from CloseAsync (EventCallbackHost precedent)
            await AwaitBoundedAsync(_renewLoop).ConfigureAwait(false);
            await AwaitBoundedAsync(_notifyWorker).ConfigureAwait(false);
#pragma warning restore VSTHRD003

            // Always de-register the SID from the routing map.
            if (!string.IsNullOrEmpty(Sid))
            {
                _owner._bySid.TryRemove(Sid, out _);
            }

            // Patch 1: read the lapse flag at the LATEST point — after the loops have drained — so a
            // concurrent device-gone / adapter-switch Lapse (renew-loop OCE path or token callback) is
            // observed and we send NO UNSUBSCRIBE for an already-lapsed subscription (AC-4.2.12/4.2.14).
            var wasLapsed = Volatile.Read(ref _lapsed) == 1;

            if (!wasLapsed && !string.IsNullOrEmpty(Sid))
            {
                // ACTIVE close → best-effort UNSUBSCRIBE over a FRESH CTS linked to the ADAPTER token
                // (NOT the just-cancelled popup token — D7, AC-4.2.13). Linking to the popup token would
                // cancel the UNSUBSCRIBE immediately.
                using var unsubCts = new CancellationTokenSource(UnsubscribeBudget);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(_owner._adapterToken, unsubCts.Token);
                try
                {
                    await _owner._http.UnsubscribeAsync(_eventSubUrl, Sid, linked.Token).ConfigureAwait(false);
                    _owner._diag.Verbose(DiagCategories.GenaUnsubscribe, "GENA UNSUBSCRIBE sent",
                        new DiagnosticContext { DeviceUuid = _deviceUuid, Url = _eventSubUrl.ToString(), Sid = Sid });
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // Swallow — popup close MUST NOT block on a hung device (FR-034 is "send", not
                    // "guarantee delivery"). AC-4.2.13.
                    _owner._diag.Warning(DiagCategories.GenaUnsubscribeFailed, "GENA UNSUBSCRIBE failed (swallowed)",
                        new DiagnosticContext { DeviceUuid = _deviceUuid, Url = _eventSubUrl.ToString(), Sid = Sid, ErrorText = ex.Message });
                }
            }
            // Lapsed close → NO UNSUBSCRIBE (AC-4.2.14): fall through to resource cleanup.

            DisposeResources();
        }

        // Await one of our own background tasks (renew loop / NOTIFY worker) with a bounded budget; a
        // timeout or residual fault is swallowed (the tasks already swallow their own exceptions, but a
        // force-on must not throw out of CloseAsync). VSTHRD003 is suppressed — these are our own tasks,
        // awaited only from CloseAsync (the EventCallbackHost.DisposeAsync precedent).
        private static async Task AwaitBoundedAsync(Task? task)
        {
            if (task is null)
            {
                return;
            }

#pragma warning disable VSTHRD003
            try { await task.WaitAsync(DrainBudget).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { /* timed out or faulted — force on */ }
#pragma warning restore VSTHRD003
        }

        public void DisposeResources()
        {
            _channel.Writer.TryComplete();
            _deviceReg.Dispose();
            _adapterReg.Dispose();
            try { _loopCts?.Dispose(); } catch (ObjectDisposedException) { /* tolerated */ }
        }
    }
}
