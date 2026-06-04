namespace ohSpy.Core.ViewModels;

using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ohSpy.Core.Collections;
using ohSpy.Core.Devices;
using ohSpy.Core.Diagnostics;
using ohSpy.Core.Events;
using ohSpy.Core.Http;
using ohSpy.Core.Models;
using ohSpy.Core.Threading;

/// <summary>
/// Subscription popup ViewModel (Story 4.3, FR-032/033/034/035/036/037). The first and only consumer
/// of the Story 4.2 <see cref="SubscriptionHandle"/> seam: it SUBSCRIBEs via <see cref="ISubscriptionClient"/>,
/// renders the parsed <see cref="EventNotification"/> stream newest-first plus a last-write-wins
/// "Latest property values" summary, shows a reason-specific banner on lapse/device-gone, and
/// UNSUBSCRIBEs on close. Mirrors the 2.9/3.2 popup pattern (CTS linked to the device token + a
/// DeviceRemoved banner + Interlocked-guarded dispose) and copies <see cref="InvocationPopupViewModel"/>'s
/// off-thread marshalling shape.
/// <para>
/// ⚠️ THE #1 HAZARD (Dev Notes §0; <c>winui-no-synccontext-marshal-vm</c>; the 3.2 RPC_E_WRONGTHREAD
/// crash class at its sharpest): <see cref="SubscriptionHandle.NotificationReceived"/> and
/// <see cref="SubscriptionHandle.Lapsed"/> are RAW <c>Action</c> events fired on the 4.2 NOTIFY-worker
/// thread (a thread-pool thread, NOT the UI thread), AND the AC-4.2.7 replay buffer flushes pre-attach
/// events/lapse INSIDE the <c>add</c> accessor — which, for this VM, runs on the OFF-THREAD post-await
/// continuation of <see cref="InitializeAsync"/>. WinUI 3 installs no <c>SynchronizationContext</c>, so
/// EVERY observable-state mutation in EVERY handler (<see cref="Events"/> append, the
/// <see cref="LatestPropertyValues"/> merge, <see cref="Status"/>/<see cref="StatusMessage"/> flips)
/// MUST be marshalled via <c>_ui.Post</c> or the bound window pokes a <c>UIElement</c> off-thread →
/// process crash. <c>_diag</c> is thread-safe so diagnostic emits may stay off-thread; only the
/// VM-state apply is marshalled. Pre-await ctor mutations run on the UI thread and are safe direct.
/// </para>
/// </summary>
public sealed partial class SubscriptionPopupViewModel : ObservableObject, IDisposable
{
    private readonly ServiceDescription _service;
    private readonly RegistryEntry _parentEntry;
    private readonly ISubscriptionClient _subscriptionClient;
    private readonly IUiDispatcher _ui;
    private readonly IDiagnosticEmitter _diag;
    private readonly IDeviceRegistry _registry;

    private readonly string _udn;                // snapshot for the FR-037 banner UDN match (OrdinalIgnoreCase)
    private readonly CancellationTokenSource _popupCts; // D7 popup level, linked to the device token
    private SubscriptionHandle? _handle;         // null until SubscribeAsync succeeds (null ⇒ no UNSUBSCRIBE)
    private int _disposed;                        // Interlocked-guarded (mirror InvocationPopupViewModel)

    /// <summary>Header label: the service-type tail (reusing the ":service:" logic).</summary>
    public string Title { get; }

    /// <summary>
    /// The raw newest-first event stream (FR-033 + D6), capped at 5,000 (the 5,001st evicts the oldest
    /// tail via <c>PrependNewest</c> → <c>Add(0)</c>+<c>Remove(5000)</c>, never <c>Reset</c>).
    /// UI-thread-owned — every mutation is marshalled (§0).
    /// </summary>
    public BoundedObservableCollection<EventNotification> Events { get; } = new(5000);

    /// <summary>
    /// Anchored "Latest property values" summary (FR-033): newest value per evented property name,
    /// last-write-wins overwrite-in-place (Dev Notes §4). Append-on-first-seen keeps the order stable
    /// so the panel does not reshuffle on every event. UI-thread-owned — mutations are marshalled (§0).
    /// </summary>
    public ObservableCollection<LatestPropertyValue> LatestPropertyValues { get; } = [];

    /// <summary>Lifecycle state (AC-4.3.1). App projects this to banner/indicator visibility (Pattern 2).</summary>
    [ObservableProperty] private SubscriptionStatus _status;

    /// <summary>Human-readable detail (granted timeout, lapse reason, or subscribe-failure text).</summary>
    [ObservableProperty] private string? _statusMessage;

    public SubscriptionPopupViewModel(
        ServiceDescription service,
        RegistryEntry parentEntry,
        ISubscriptionClient subscriptionClient,
        IUiDispatcher ui,
        IDiagnosticEmitter diag,
        IDeviceRegistry registry)
    {
        _service = service;
        _parentEntry = parentEntry;
        _subscriptionClient = subscriptionClient;
        _ui = ui;
        _diag = diag;
        _registry = registry;

        _udn = parentEntry.Udn;
        Title = ComputeServiceTail(service);

        // AC-4.3.2 #1: Subscribing set synchronously in the ctor (the ctor runs on the UI thread).
        _status = SubscriptionStatus.Subscribing;

        // D7: link the popup CTS to the PUBLIC device token (DeviceCts is internal). Device removal /
        // adapter switch cancels DeviceToken → cancels this → the in-flight SubscribeAsync / renew loop
        // observes cancellation (OCE swallowed; or a lapse cascades through the handle).
        _popupCts = CancellationTokenSource.CreateLinkedTokenSource(parentEntry.DeviceToken);

        // FR-037 banner (the 2.9 pattern): DeviceRemoved fires on the UI thread; a UDN match (OrdinalIgnoreCase)
        // flips the banner. IDisposable unsubscribes — without it the singleton registry pins every popup VM ever
        // opened (Story 2.9's hard lesson). NOTE the dual path to DeviceGone — DeviceRemoved (registry,
        // UI-thread) AND handle.Lapsed(DeviceGone) (4.2, off-thread); both converge on Status = DeviceGone
        // (the apply is idempotent).
        _registry.DeviceRemoved += OnDeviceRemoved;
    }

    /// <summary>
    /// SUBSCRIBE flow (AC-4.3.2/.5). Kicked off fire-and-forget by the App launcher AFTER
    /// <c>window.Activate()</c> + <c>Adopt(...)</c>. Mirrors <see cref="InvocationPopupViewModel.InitializeAsync"/>:
    /// <c>ConfigureAwait(false)</c> on the await; on the OFF-THREAD post-await continuation, marshal EVERY
    /// observable mutation via <c>_ui.Post</c>. The handlers are attached INSIDE the marshalled block so
    /// the 4.2 replay-buffer flush (fired synchronously during <c>add</c>) is delivered — and because that
    /// flush is itself off-thread, the handlers' own <c>_ui.Post</c> covers it (§0).
    /// </summary>
    public async Task InitializeAsync()
    {
        try
        {
            var handle = await _subscriptionClient
                .SubscribeAsync(_service, _parentEntry, _popupCts.Token)
                .ConfigureAwait(false);

            // Marshal the success apply onto the UI thread. Attaching the handlers here flushes any
            // pre-attach replay events/lapse synchronously (off-thread) — the handlers re-marshal.
            _ui.Post(() =>
            {
                // If the popup was disposed during the await, do not attach (Dispose already ran its
                // once-guard); just best-effort close the freshly-returned handle.
                if (Volatile.Read(ref _disposed) == 1)
                {
                    _ = handle.CloseAsync();
                    return;
                }

                _handle = handle;
                handle.NotificationReceived += OnNotification;
                handle.Lapsed += OnLapsed;

                // Only advance to Subscribed if a lapse/device-gone replay did not already move us past it.
                if (Status == SubscriptionStatus.Subscribing)
                {
                    Status = SubscriptionStatus.Subscribed;
                    StatusMessage = $"SID {handle.Sid}";
                }
            });
        }
        catch (OperationCanceledException)
        {
            // AC-4.3.5: popup closed (or device gone) during subscribe. NOT a failure — no status flip,
            // no diagnostic (mirror InvokeAsync / InitializeAsync convention). No handle ⇒ close does
            // no UNSUBSCRIBE.
        }
        catch (UpnpException ex)
        {
            // AC-4.3.5: typed transport/timeout/protocol subscribe failure → FailedToSubscribe banner.
            FailSubscribe(ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // NFR-R3 broad defensive catch: any other failure is a failed-subscribe (no diagnostic —
            // no typed context for an unknown failure). The popup stays closeable; no handle ⇒ no UNSUBSCRIBE.
            FailSubscribe($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // AC-4.3.5: marshal the failed-subscribe terminal state (the catch runs off-thread).
    private void FailSubscribe(string message) =>
        _ui.Post(() =>
        {
            Status = SubscriptionStatus.FailedToSubscribe;
            StatusMessage = message;
        });

    /// <summary>
    /// AC-4.3.3/.6: a parsed NOTIFY arrived (raised on the 4.2 NOTIFY-worker thread, OR synchronously
    /// during the replay flush — both off-thread). Marshal: prepend newest + last-write-wins latest-values
    /// merge. No XML parse here — <see cref="EventNotification.Properties"/> is already the parsed dictionary.
    /// </summary>
    private void OnNotification(EventNotification n) =>
        _ui.Post(() =>
        {
            Events.PrependNewest(n);
            MergeLatest(n.Properties);
        });

    // Last-write-wins merge over the LatestPropertyValues rows. Overwrite an existing name in place
    // (raises PropertyChanged on the row's Value → the bound text updates, no reshuffle); append a new
    // row on first sight (append-on-first-seen → stable order). Runs ON the UI thread (called inside
    // the OnNotification Post).
    private void MergeLatest(IReadOnlyDictionary<string, string> properties)
    {
        foreach (var kvp in properties)
        {
            var existing = FindRow(kvp.Key);
            if (existing is not null)
                existing.Value = kvp.Value;
            else
                LatestPropertyValues.Add(new LatestPropertyValue(kvp.Key, kvp.Value));
        }
    }

    private LatestPropertyValue? FindRow(string name)
    {
        foreach (var row in LatestPropertyValues)
            if (row.Name == name)
                return row;
        return null;
    }

    /// <summary>
    /// AC-4.3.4: the subscription lapsed (raised off-thread by the 4.2 renew loop, OR via the replay
    /// flush). Marshal a reason-specific banner; the popup stays open and closeable, already-shown
    /// events/values remain.
    /// </summary>
    private void OnLapsed(SubscriptionLapseReason reason) =>
        _ui.Post(() =>
        {
            switch (reason)
            {
                case SubscriptionLapseReason.DeviceGone:
                    Status = SubscriptionStatus.DeviceGone;
                    StatusMessage = "device no longer reachable";
                    break;
                case SubscriptionLapseReason.AdapterSwitch:
                    Status = SubscriptionStatus.Lapsed;
                    StatusMessage = "device unreachable after adapter switch";
                    break;
                case SubscriptionLapseReason.RenewRefused:
                case SubscriptionLapseReason.RenewTransportError:
                default:
                    Status = SubscriptionStatus.Lapsed;
                    StatusMessage = "subscription lapsed (renewal refused / failed)";
                    break;
            }
        });

    // FR-037 banner (the 2.9 pattern): DeviceRemoved fires on the UI thread. UDN match (OrdinalIgnoreCase)
    // → idempotent Status = DeviceGone. The handle ALSO raises Lapsed(DeviceGone) (4.2, off-thread) → both
    // converge here on DeviceGone; the apply is idempotent (a second DeviceGone is a harmless re-set).
    private void OnDeviceRemoved(string udn)
    {
        if (!string.Equals(udn, _udn, StringComparison.OrdinalIgnoreCase) || Status == SubscriptionStatus.DeviceGone) return;
        Status = SubscriptionStatus.DeviceGone;
        StatusMessage = "device no longer reachable";
    }

    /// <summary>
    /// AC-4.3.9 popup-close cascade (D7). Called by the window's Closed handler. Idempotent
    /// (Interlocked once-guard): cancel the popup CTS → detach the handle events + the registry
    /// subscription → fire-and-forget <c>handle.CloseAsync()</c> (4.2 runs the best-effort
    /// UNSUBSCRIBE over the adapter-linked token; on a lapsed/device-gone/failed-subscribe handle it
    /// sends no UNSUBSCRIBE) → dispose the CTS in a <c>finally</c>. The window closes immediately; the
    /// ≤5 s UNSUBSCRIBE runs async and does not block the close.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        try
        {
            _popupCts.Cancel();
            _registry.DeviceRemoved -= OnDeviceRemoved;
            if (_handle is { } handle)
            {
                handle.NotificationReceived -= OnNotification;
                handle.Lapsed -= OnLapsed;
                _ = handle.CloseAsync(); // fire-and-forget (idempotent; UNSUBSCRIBE over the adapter token)
            }
        }
        finally
        {
            _popupCts.Dispose();
        }
    }

    // Service-type tail after ":service:" (e.g. "RenderingControl:1"), falling back to the verbatim
    // serviceType then serviceId. Same logic as ServiceNodeViewModel.ComputeLabel / InvocationPopupViewModel.
    private static string ComputeServiceTail(ServiceDescription service)
    {
        const string marker = ":service:";
        var type = service.ServiceType;
        var idx = type.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            var tail = type[(idx + marker.Length)..];
            if (tail.Length > 0) return tail;
        }
        if (type.Length > 0) return type;
        return service.ServiceId ?? "(service)";
    }
}
