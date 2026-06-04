namespace ohSpy.Core.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using ohSpy.Core.Collections;
using ohSpy.Core.Discovery;
using ohSpy.Core.Models;
using ohSpy.Core.Threading;

/// <summary>
/// Story 2.7 — the SSDP log right pane (FR-003 / FR-014 / FR-015). Subscribes to the
/// discovery service and projects every NOTIFY <c>ssdp:alive</c> / <c>ssdp:byebye</c>
/// announcement into a newest-first, 10,000-capacity <see cref="BoundedObservableCollection{T}"/>
/// (FR-016 + D6). Mirrors the <c>DiagnosticRingSink</c> precedent
/// (BoundedObservableCollection + IUiDispatcher.Post(PrependNewest)).
/// <para>
/// <see cref="IsAtTop"/> is the testable half of the FR-055 smart auto-follow rule; the
/// scroll mechanics that read/write it live in the view (MainWindow code-behind).
/// </para>
/// </summary>
public sealed partial class SsdpLogViewModel : ObservableObject, IDisposable
{
    // FR-016 + D6 cap.
    private const int Capacity = 10_000;

    private readonly IDiscoveryService _discovery;
    private readonly IUiDispatcher _ui;
    private int _disposed;

    // True when the bound list is parked at (or near) the top — drives smart auto-follow
    // (FR-055). Set by the view's ScrollViewer.ViewChanged handler; read by the view's
    // new-arrival handler to decide whether to keep the visual anchored at the top.
    [ObservableProperty]
    private bool _isAtTop = true; // starts at top (empty list)

    public BoundedObservableCollection<SsdpLogEntry> Entries { get; } = new(Capacity);

    public SsdpLogViewModel(IDiscoveryService discovery, IUiDispatcher ui)
    {
        _discovery = discovery;
        _ui = ui;
        _discovery.AnnouncementReceived += OnAnnouncementReceived;
    }

    // AnnouncementReceived already fires on the UI thread (DiscoveryService routes via
    // IUiDispatcher.Post), but we marshal the prepend through IUiDispatcher.Post anyway —
    // per the AC and the DiagnosticRingSink precedent. It is a cheap same-thread re-queue
    // that keeps the VM decoupled from the event's threading guarantee. DispatcherQueue is
    // FIFO, so arrival order is preserved. Do NOT add a second marshal.
    private void OnAnnouncementReceived(SsdpAnnouncement ann)
    {
        var kind = ClassifyOrNull(ann.NTS);
        if (kind is null) return; // FR-014/FR-015 grammar: only ssdp:alive / ssdp:byebye

        // Stamp at receipt: AnnouncementReceived carries no arrival time, and there is no
        // clock abstraction in Core (DiagnosticEmitter uses DateTime.UtcNow directly). The
        // event fires on the UI thread shortly after arrival, so the skew is sub-ms.
        // ann.Udn is ALREADY extracted from the USN by SsdpParser — do not re-parse. An absent UDN
        // renders empty (Amendment A30 — the old all-zero Guid.Empty fallback is gone).
        var entry = new SsdpLogEntry(DateTime.UtcNow, kind.Value, ann.Udn ?? "");
        _ui.Post(() => Entries.PrependNewest(entry));
    }

    // Log routing is NTS-only and narrower than the registry routing in
    // DiscoveryService.RouteOnUiThread: absent NTS (M-SEARCH responses) is NOT logged.
    private static SsdpLogKind? ClassifyOrNull(string? nts) => nts switch
    {
        not null when nts.Equals("ssdp:alive", StringComparison.OrdinalIgnoreCase)
            => SsdpLogKind.Alive,
        not null when nts.Equals("ssdp:byebye", StringComparison.OrdinalIgnoreCase)
            => SsdpLogKind.Byebye,
        _ => null,
    };

    /// <summary>
    /// Drop all log rows (single Reset — AC-6.6). Forward-compat for the FR-050 adapter
    /// switch, which Story 5.2 wires to call this. UI-thread-owned; callers on the UI thread.
    /// </summary>
    public void Clear() => Entries.Clear();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _discovery.AnnouncementReceived -= OnAnnouncementReceived;
    }
}
