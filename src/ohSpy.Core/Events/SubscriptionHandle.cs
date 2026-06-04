namespace ohSpy.Core.Events;

using ohSpy.Core.Models;

/// <summary>
/// The single object Story 4.3's subscription-popup VM holds onto for one GENA subscription
/// (Story 4.2, AC-4.2.2; epic L1594). It surfaces the granted <see cref="Sid"/>, a stream of parsed
/// <see cref="EventNotification"/>s, a <see cref="Lapsed"/> signal, and an idempotent
/// <see cref="CloseAsync"/>.
/// <para>
/// <see cref="NotificationReceived"/> and <see cref="Lapsed"/> are <b>raw Core events</b>
/// (<c>Action&lt;…&gt;</c>, NOT marshalled). 4.2 stays non-UI; 4.3's popup VM is responsible for
/// <c>_ui.Post</c> marshalling onto bound state (retro Action H / memory
/// <c>winui-no-synccontext-marshal-vm</c>). The handle delegates the actual UNSUBSCRIBE-on-close
/// (the D7 "cleanup uses the level-above token" invariant) back to the <c>SubscriptionClient</c> via
/// a close-delegate supplied at construction.
/// </para>
/// </summary>
public sealed class SubscriptionHandle
{
    private readonly Func<Task> _closeDelegate;
    private readonly object _gate = new();
    private readonly Queue<EventNotification> _replayBuffer = new();
    private Action<EventNotification>? _notificationReceived;
    private int _closed;

    internal SubscriptionHandle(string sid, Func<Task> closeDelegate)
    {
        Sid = sid;
        _closeDelegate = closeDelegate;
    }

    /// <summary>The subscription identifier granted by the device (the <c>SID:</c> header).</summary>
    public string Sid { get; }

    /// <summary>
    /// Raised (raw, off the host's/worker's thread) for each parsed event delivered to this
    /// subscription. 4.3 marshals onto bound state. <b>Replay guarantee (AC-4.2.7):</b> any event
    /// that arrived (e.g. a NOTIFY-before-SID-race event replayed at registration) BEFORE the first
    /// subscriber attached is flushed to that first subscriber immediately on subscription — so the
    /// consumer never loses an event delivered between SUBSCRIBE returning and its handler attaching.
    /// </summary>
    public event Action<EventNotification>? NotificationReceived
    {
        add
        {
            EventNotification[]? backlog = null;
            lock (_gate)
            {
                _notificationReceived += value;
                if (_replayBuffer.Count > 0 && value is not null)
                {
                    backlog = _replayBuffer.ToArray();
                    _replayBuffer.Clear();
                }
            }

            if (backlog is not null && value is not null)
            {
                foreach (var n in backlog)
                {
                    value(n);
                }
            }
        }
        remove
        {
            lock (_gate)
            {
                _notificationReceived -= value;
            }
        }
    }

    private Action<SubscriptionLapseReason>? _lapsed;
    private SubscriptionLapseReason? _pendingLapse;

    /// <summary>
    /// Raised once when the subscription stops delivering events without an explicit close
    /// (renew refused/failed, adapter switch, device gone). After a lapse, <see cref="CloseAsync"/>
    /// sends no UNSUBSCRIBE. <b>Replay guarantee:</b> a lapse that fired before the first subscriber
    /// attached is flushed to that subscriber on attach (closes the renew-loop-vs-handler-attach race;
    /// also matches the real 4.3 consumer which attaches synchronously post-await).
    /// </summary>
    public event Action<SubscriptionLapseReason>? Lapsed
    {
        add
        {
            SubscriptionLapseReason? pending = null;
            lock (_gate)
            {
                _lapsed += value;
                if (_pendingLapse is not null && value is not null)
                {
                    pending = _pendingLapse;
                    _pendingLapse = null;
                }
            }

            if (pending is not null && value is not null)
            {
                value(pending.Value);
            }
        }
        remove
        {
            lock (_gate)
            {
                _lapsed -= value;
            }
        }
    }

    /// <summary>
    /// Tears down the subscription. On an ACTIVE subscription this best-effort UNSUBSCRIBEs over a
    /// fresh CTS linked to the adapter token (D7 level-above invariant). On a LAPSED subscription it
    /// sends no UNSUBSCRIBE. Always de-registers the SID. <b>Idempotent</b> — a second call is a safe
    /// no-op (AC-4.2.2).
    /// </summary>
    public Task CloseAsync()
    {
        // Idempotent guard — only the first caller runs the (single) close delegate.
        if (Interlocked.Exchange(ref _closed, 1) == 1)
        {
            return Task.CompletedTask;
        }

        return _closeDelegate();
    }

    // ── Internal raise helpers (the client invokes these) ───────────────────────
    internal void RaiseNotification(EventNotification notification)
    {
        Action<EventNotification>? handler;
        lock (_gate)
        {
            handler = _notificationReceived;
            if (handler is null)
            {
                // No subscriber yet — buffer for replay to the first subscriber (AC-4.2.7 race close-out).
                _replayBuffer.Enqueue(notification);
                return;
            }
        }

        handler(notification);
    }

    internal void RaiseLapsed(SubscriptionLapseReason reason)
    {
        Action<SubscriptionLapseReason>? handler;
        lock (_gate)
        {
            handler = _lapsed;
            if (handler is null)
            {
                // No subscriber yet — record the lapse for replay to the first subscriber.
                _pendingLapse ??= reason;
                return;
            }
        }

        handler(reason);
    }

    /// <summary>True once <see cref="CloseAsync"/> has been invoked (test seam / internal guard).</summary>
    internal bool IsClosed => Volatile.Read(ref _closed) == 1;
}
