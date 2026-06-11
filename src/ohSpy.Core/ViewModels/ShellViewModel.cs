namespace ohSpy.Core.ViewModels;

using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ohSpy.Core.Devices;
using ohSpy.Core.Diagnostics;
using ohSpy.Core.Discovery;
using ohSpy.Core.Events;
using ohSpy.Core.Models;
using ohSpy.Core.Threading;

public sealed partial class ShellViewModel : ObservableObject, IAsyncDisposable
{
    // The FR-050 per-scope teardown budget (transport dispose, step 2). Null ⇒ AdapterScope's own 2 s
    // default; a test seam shrinks it so the AdapterSwitchTimeout overrun path is testable fast.
    private TimeSpan? _adapterTeardownBudget;

    /// <summary>Test seam (InternalsVisibleTo): shrink the per-scope transport-teardown budget (step 2).</summary>
    internal void SetAdapterTeardownBudgetForTest(TimeSpan budget) => _adapterTeardownBudget = budget;

    // Single scope-construction point (startup + switch) so the test teardown budget applies to both.
    private AdapterScope NewScope() =>
        _adapterTeardownBudget is { } b
            ? new AdapterScope(_adapterEnum, _transportFactory, _diag, b, _appToken)
            : new AdapterScope(_adapterEnum, _transportFactory, _diag, _appToken);

    private readonly INetworkAdapterEnumerator _adapterEnum;
    private readonly Func<ISsdpTransport> _transportFactory;
    private readonly Func<IEventCallbackHost> _callbackHostFactory;
    private readonly IDiscoveryService _discovery;
    private readonly ISubscriptionClient _subscriptionClient;
    private readonly IDeviceRegistry _registry;
    private readonly IUiDispatcher _ui;
    private readonly IDiagnosticEmitter _diag;
    private readonly IDiagnosticsLauncher _diagnosticsLauncher;
    private readonly INetworkChangeNotifier _networkChangeNotifier; // FR-057 (Story 2.12)

    private AdapterScope? _adapterScope;
    private IEventCallbackHost? _callbackHost; // A23: owned here, rebuilt on switch (cannot re-start after dispose)
    private Task? _runTask;
    private int _started;
    private int _switching; // re-entrancy guard (AC-5.2.9): 0 idle, 1 a switch (or startup) in flight

    [ObservableProperty]
    private DeviceTreeViewModel _deviceTree;

    [ObservableProperty]
    private SsdpLogViewModel _ssdpLog;

    /// <summary>
    /// True while an adapter switch is in flight (AC-5.2.2 transient / AC-5.2.9 menu-disable). The App
    /// View menu binds this to disable the adapter items and show a "Switching adapter…" hint. Mutated
    /// only on the UI thread (set synchronously pre-await; cleared via <see cref="IUiDispatcher.Post"/>).
    /// </summary>
    [ObservableProperty]
    private bool _isSwitching;

    /// <summary>
    /// True while a Story 5.3 rescan is in flight (AC-5.3.3 menu-disable / AC-5.3.4 "Rescanning…"
    /// transient). Drives <see cref="RescanCommand"/>'s CanExecute so the bound View → Rescan
    /// <c>MenuFlyoutItem</c> auto-disables, and the App may bind it to an inline spinner. Mutated only on
    /// the UI thread (set synchronously pre-await at the top of the command; cleared via
    /// <see cref="IUiDispatcher.Post"/> since the post-await continuation resumes off-thread).
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RescanCommand))]
    private bool _isRescanning;

    /// <summary>The default rescan M-SEARCH MX (FR-022) — parity with the startup search budget.</summary>
    private static readonly TimeSpan RescanMx = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Grace added to the MX wait (Q3) so a device that responds right at the MX edge isn't pruned by a
    /// routing-latency race (its alive must land + route through <c>_ui.Post</c> before the prune snapshot).
    /// </summary>
    private static readonly TimeSpan RescanGrace = TimeSpan.FromMilliseconds(500);

    // The MX-wait delay seam (the SubscriptionClient._delay precedent). Real Task.Delay in production;
    // a test swaps it (SetRescanDelayForTest) so the MX window is instant/controllable — no real 5 s sleep.
    private Func<TimeSpan, CancellationToken, Task> _rescanDelay = (d, ct) => Task.Delay(d, ct);

    /// <summary>Test seam (InternalsVisibleTo): replace the rescan MX-wait delay (no real 5 s sleep).</summary>
    internal void SetRescanDelayForTest(Func<TimeSpan, CancellationToken, Task> delay) => _rescanDelay = delay;

    // ── FR-057 (Story 2.12): host network-change auto-rebind ──────────────────────────────────────────
    // The debounce window for the burst of NetworkAddressChanged events a transition produces (Q2 default
    // 2 s, trailing-edge). NOT load-bearing for correctness (a too-short window just re-triggers on the
    // next event); test-settable so tests drive it to (effectively) zero — no real multi-second sleeps.
    private TimeSpan _debounceWindow = TimeSpan.FromSeconds(2);

    // The debounce delay seam (the SubscriptionClient._delay / _rescanDelay precedent). Real Task.Delay
    // in production; a test swaps it (SetNetworkChangeDebounceForTest) so the window is instant/controllable.
    private Func<TimeSpan, CancellationToken, Task> _networkChangeDebounce = (d, ct) => Task.Delay(d, ct);

    // Trailing-edge debounce CTS: each NetworkAddressChanged cancels the prior pending evaluation and
    // starts a fresh window, so a burst coalesces into ONE evaluation (AC #4).
    private CancellationTokenSource? _debounceCts;

    // Test determinism (InternalsVisibleTo): the off-thread NetworkAddressChanged handler is
    // fire-and-forget, so a test needs a handle to await the debounce → marshal → evaluate chain rather
    // than racing it. The handler records its in-flight DebouncedEvaluateAsync task here; the evaluation
    // (run inside the marshalled Post) records its own task. The seam below joins both.
    private volatile Task _lastDebounceTask = Task.CompletedTask;
    private volatile Task _lastEvaluateTask = Task.CompletedTask;

    /// <summary>
    /// Test seam (InternalsVisibleTo): await the most recently scheduled debounce → evaluate chain
    /// (FR-057). Awaits the debounce task (which posts the evaluation) and then the evaluation task, so a
    /// test can deterministically assert the post-rebind state without sleeping. With an InlineUiDispatcher
    /// the Post runs synchronously inside the debounce task, capturing the evaluate task before this returns,
    /// so a single call suffices. With a DeferredUiDispatcher the post is QUEUED, not run: the first call
    /// here awaits only the (completed) debounce task while <see cref="_lastEvaluateTask"/> is still
    /// Task.CompletedTask — drain the dispatcher, THEN call again to await the real evaluation. (See the
    /// off-thread / Action-H marshalling tests for the two-call-around-Drain() pattern.)
    /// </summary>
    internal async Task WaitForNetworkChangeEvaluationForTestAsync()
    {
        // VSTHRD003: these are deliberately fire-and-forget tasks (the off-thread handler cannot await
        // them in production); the test seam joins them to make assertions deterministic — no deadlock
        // (no UI-thread affinity is captured; the test rig's dispatcher is inline/deferred/gated).
#pragma warning disable VSTHRD003
        await _lastDebounceTask.ConfigureAwait(false);
        await _lastEvaluateTask.ConfigureAwait(false);
#pragma warning restore VSTHRD003
    }

    /// <summary>
    /// Test seam (InternalsVisibleTo): replace the network-change debounce delay (no real 2 s sleep) and
    /// optionally shrink the window. Mirrors <see cref="SetRescanDelayForTest"/>.
    /// </summary>
    internal void SetNetworkChangeDebounceForTest(Func<TimeSpan, CancellationToken, Task> delay, TimeSpan? window = null)
    {
        _networkChangeDebounce = delay;
        if (window is { } w)
        {
            _debounceWindow = w;
        }
    }

    public ShellViewModel(
        INetworkAdapterEnumerator adapterEnum,
        Func<ISsdpTransport> transportFactory,
        Func<IEventCallbackHost> callbackHostFactory,
        IDiscoveryService discovery,
        ISubscriptionClient subscriptionClient,
        IDeviceRegistry registry,
        IUiDispatcher ui,
        IDiagnosticEmitter diag,
        IDiagnosticsLauncher diagnosticsLauncher,
        INetworkChangeNotifier networkChangeNotifier,
        NodeServices nodeServices)
    {
        _adapterEnum  = adapterEnum;
        _transportFactory = transportFactory;
        _callbackHostFactory = callbackHostFactory;
        _discovery    = discovery;
        _subscriptionClient = subscriptionClient;
        _registry     = registry;
        _ui           = ui;
        _diag         = diag;
        _diagnosticsLauncher = diagnosticsLauncher;
        _networkChangeNotifier = networkChangeNotifier;
        _deviceTree  = new DeviceTreeViewModel(registry, ui, nodeServices);
        _ssdpLog     = new SsdpLogViewModel(discovery, ui); // subscribes to AnnouncementReceived (app-lifetime)
    }

    /// <summary>
    /// Story 5.1 (FR-041): open the Diagnostics viewer from the View menu. Delegates to the App-side
    /// launcher across the Core/App boundary (Pattern 2 forbids Core → App; ShellViewModel is Core and
    /// cannot <c>new</c> a WinUI Window). The launcher applies the canonical Activate-then-Adopt
    /// sequence and re-activates the existing single viewer if already open.
    /// </summary>
    [RelayCommand]
    private void OpenDiagnostics() => _diagnosticsLauncher.Open();

    /// <summary>
    /// Story 5.3 (FR-021..FR-024): the View → Rescan action. Re-issues the M-SEARCH on the current
    /// adapter via the scope-owned transport, waits MX (+ a small grace) WITHOUT suspending the live
    /// unsolicited-NOTIFY listener (no socket teardown, no <c>DiscoveryService</c> suspension, no
    /// registry/host/subscription teardown — this is NOT the 5.2 switch), then prunes every device not
    /// seen since the pre-send epoch (responders + in-window alives refreshed their <c>LastSeenUtc</c> via
    /// <c>OnAlive</c> and survive). Fire-and-forget from the menu (A26): the body handles its own
    /// exceptions.
    /// <para>
    /// <b>Two guards, switch wins (AC-5.3.10):</b> rescan uses its OWN guard (<see cref="IsRescanning"/> /
    /// CanExecute), SEPARATE from the <c>_switching</c> startup/switch guard — a switch is never blocked by
    /// an in-flight rescan. A concurrent adapter switch cancels the shared <c>_adapterCts</c> (inside
    /// <c>AdapterScope.DisposeAsync</c>); the MX wait is linked to that token, so the switch aborts the
    /// rescan (OCE swallowed, Warning emitted, NO prune against the fresh post-switch registry). A rescan
    /// fired mid-switch no-ops on the null-scope guard below.
    /// </para>
    /// <para>
    /// <b>Marshalling (Action H / winui-no-synccontext-marshal-vm):</b> the body resumes off-thread after
    /// the first <c>await</c>. <see cref="IsRescanning"/> is set <c>true</c> synchronously (the command
    /// starts on the UI thread); the prune (which mutates the bound tree via <c>DeviceRemoved</c>) and the
    /// <see cref="IsRescanning"/> clear are applied THROUGH <see cref="IUiDispatcher.Post"/>.
    /// </para>
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRescan))]
    private async Task RescanAsync()
    {
        // Re-entrancy guard (AC-5.3.3). CanExecute already disables the menu item, but a programmatic
        // second invocation is still a silent no-op (no overlapping rescans).
        if (IsRescanning)
        {
            return;
        }

        // Zero-adapter / mid-switch no-op (AC-5.3.5 / NFR-R5): nothing to scan, and a mid-teardown scope
        // must not be poked. Read the scope once.
        var scope = _adapterScope;
        if (scope is null || scope.CurrentAdapterIPv4 is null)
        {
            return;
        }

        // Transient state (AC-5.3.4). Pre-await → on the UI thread → safe direct set. The CanExecute
        // change (NotifyCanExecuteChangedFor) disables the bound MenuFlyoutItem.
        IsRescanning = true;
        _diag.Information(DiagCategories.Rescan, "rescan started");

        // Stamp the epoch BEFORE the send (AC-5.3.7). Responses arrive AFTER the send, so their arrival
        // UTC ≥ epoch → LastSeenUtc ≥ epoch → they survive the prune.
        var epochUtc = DateTime.UtcNow;
        var token = scope.AdapterToken; // the scope-owned token a concurrent switch cancels (AC-5.3.10)

        try
        {
            await scope.SendMSearchAsync(RescanMx).ConfigureAwait(false);
            await _rescanDelay(RescanMx + RescanGrace, token).ConfigureAwait(false);

            // Prune + completion diagnostic + transient clear, ALL marshalled together (Action H). Under a
            // deferred dispatcher none of this applies until the UI thread drains (AC-5.3.12).
            _ui.Post(() =>
            {
                var pruned = _registry.PruneNotSeenSince(epochUtc);
                _diag.Information(DiagCategories.Rescan, $"rescan pruned {pruned} non-responders");
                IsRescanning = false;
            });
        }
        catch (OperationCanceledException)
        {
            // The adapter switch won the shared token (AC-5.3.10): abandon the rescan, do NOT prune against
            // the fresh post-switch registry. The clear is marshalled (off-thread continuation).
            _diag.Warning(DiagCategories.Rescan, "rescan abandoned — adapter switch in progress");
            _ui.Post(() => IsRescanning = false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _diag.Warning(DiagCategories.Rescan, "rescan failed",
                new DiagnosticContext { ErrorText = ex.Message });
            _ui.Post(() => IsRescanning = false);
        }
    }

    /// <summary>CanExecute for <see cref="RescanCommand"/> (AC-5.3.3): no overlapping rescans.</summary>
    private bool CanRescan() => !IsRescanning;

    /// <summary>The currently-bound adapter IPv4 (null before startup / on the zero-adapter host).</summary>
    public System.Net.IPAddress? CurrentAdapterIPv4 => _adapterScope?.CurrentAdapterIPv4;

    /// <summary>
    /// True when <paramref name="adapter"/> is the currently-active one (drives the View menu's
    /// <c>RadioMenuFlyoutItem.IsChecked</c>). Compared by IPv4 (the bind address, AC-5.2.1).
    /// </summary>
    public bool IsCurrentAdapter(NetworkAdapter adapter) =>
        adapter is not null && adapter.IPv4.Equals(CurrentAdapterIPv4);

    /// <summary>Enumerates the eligible adapters for the View menu (FR-048 / AC-5.2.1).</summary>
    public IReadOnlyList<NetworkAdapter> EnumerateAdapters() => _adapterEnum.Enumerate();

    // Called from App.OnLaunched (fire-and-forget, Amendment A26 pattern).
    // Constructs and starts the launch-default AdapterScope; starts the bound services after scope is live.
    // Single-call: a second invocation is a no-op so it can't orphan the first scope.
    public Task StartAsync(CancellationToken appToken)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
            return Task.CompletedTask;

        // The re-entrancy guard also rejects a switch fired during startup (AC-5.2.9): hold it for the
        // duration of startup; RunStartAsync releases it in its finally.
        Interlocked.Exchange(ref _switching, 1);
        _appToken = appToken;
        _adapterScope = NewScope();

        // FR-057 (Story 2.12): arm the host network-change listener now the scope exists. Subscribed HERE
        // (not in the ctor) so a never-started VM in a test does not arm the BCL handler. DisposeAsync
        // detaches + disposes it (the leak guard). The raw handler only schedules the debounce — it does
        // NO enumerate/rebind work inline (AC #1), and the event fires off-thread (Action H).
        _networkChangeNotifier.NetworkAddressChanged += OnNetworkAddressChanged;

        // _runTask is retained so DisposeAsync can await orderly startup completion before
        // tearing the scope down (avoids disposing mid-bind).
        _runTask = RunStartAsync(_adapterScope, preferred: null);
        return Task.CompletedTask;
    }

    private CancellationToken _appToken;

    // Test seam (InternalsVisibleTo): await orderly startup completion so switch tests are deterministic.
    internal Task WaitForStartupAsync() => _runTask ?? Task.CompletedTask;

    // Test seam (InternalsVisibleTo): the live adapter scope token, used by the AC-7.1 cancellation
    // drill to link simulated device fetches to the same token the switch cancels.
    internal CancellationToken CurrentAdapterTokenForTest() =>
        _adapterScope?.AdapterToken ?? CancellationToken.None;

    private async Task RunStartAsync(AdapterScope scope, NetworkAdapter? preferred)
    {
        try
        {
            await scope.StartAsync(preferred).ConfigureAwait(false);
            await StartBoundServicesAsync(scope).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _diag.Warning(DiagCategories.AdapterSwitch,
                "adapter startup failed — no SSDP traffic",
                new DiagnosticContext { ErrorText = ex.Message });
        }
        finally
        {
            // Startup complete (or failed): release the guard so the menu / a switch can proceed.
            Interlocked.Exchange(ref _switching, 0);
        }
    }

    /// <summary>
    /// The shared start block (reused by startup AND the Story 5.2 switch — FR-050 steps 9/10, do NOT
    /// duplicate). Once the scope has bound an adapter: construct + start the GENA callback host on the
    /// bound IP, hand the live host + adapter token to the subscription client, and (re)bind discovery to
    /// the scope-owned transport reader. M-SEARCH (step 10) already fired inside <c>scope.StartAsync</c>.
    /// On the zero-adapter host (CurrentAdapterIPv4 null) nothing inbound is started (NFR-R5).
    /// </summary>
    private async Task StartBoundServicesAsync(AdapterScope scope)
    {
        if (scope.CurrentAdapterIPv4 is null)
        {
            return; // zero-adapter host still runs; no inbound listener / discovery
        }

        // Story 4.1 — construct a FRESH callback host (A23: the host cannot re-start after dispose) and
        // bind it on the bound adapter IP, before discovery. ShellViewModel owns its lifetime.
        var host = _callbackHostFactory();
        await host.StartAsync(scope.CurrentAdapterIPv4, scope.AdapterToken).ConfigureAwait(false);
        _callbackHost = host;

        // Story 4.2 — hand the LIVE host + the per-AdapterScope token to the singleton subscription
        // client (re-points CallbackBaseUrl + re-subscribes NotifyReceived to the new host, and re-arms
        // the adapter token for future subs). The OLD adapter token's cancel (on switch) has already
        // lapsed every live sub with AdapterSwitch before we get here.
        _subscriptionClient.SetCallbackHost(host);
        _subscriptionClient.SetAdapterContext(scope.AdapterToken);

        // Story 2.4 / A23 — (re)bind discovery to the scope-owned transport reader. On startup the
        // singleton service Starts; on the switch it Rebinds (drains the old loop, fresh loop on the new
        // reader) so SsdpLogViewModel's app-lifetime AnnouncementReceived subscription stays valid.
        if (_discoveryStarted)
        {
            await _discovery.RebindAsync(scope.IncomingDatagrams, scope.AdapterToken, scope.AdapterToken)
                            .ConfigureAwait(false);
        }
        else
        {
            await _discovery.StartAsync(scope.IncomingDatagrams, scope.AdapterToken, scope.AdapterToken)
                            .ConfigureAwait(false);
            _discoveryStarted = true;
        }
    }

    private bool _discoveryStarted;

    /// <summary>
    /// FR-050 / D7 atomic adapter switch (AC-5.2.2/.3/.4/.8/.9). Invoked fire-and-forget from the View
    /// menu (the body handles its own exceptions — A26 discipline). Tears down the old adapter scope +
    /// callback host, cancels in-flight fetches + every subscription, clears the registry + SSDP log,
    /// rebinds on <paramref name="newAdapter"/>, and re-runs discovery — within the 2 s budget.
    /// <para>
    /// Marshalling (retro Action H): the pre-<c>await</c> body runs on the UI thread (the menu), but the
    /// post-await continuation resumes off-thread (WinUI has no SynchronizationContext). The
    /// <see cref="IsSwitching"/> set is synchronous (UI thread); the clear at the end is marshalled via
    /// <c>_ui.Post</c>. <c>DeviceRegistry.Clear()</c> + <c>SsdpLogViewModel.Clear()</c> are called on the
    /// UI thread via <c>_ui.Post</c>.
    /// </para>
    /// </summary>
    public Task SwitchAdapterAsync(NetworkAdapter newAdapter)
    {
        ArgumentNullException.ThrowIfNull(newAdapter);

        // No-op if the chosen adapter is already active (AC-5.2.2). Read before taking the guard.
        if (newAdapter.IPv4.Equals(CurrentAdapterIPv4))
        {
            return Task.CompletedTask;
        }

        return SwitchCoreAsync(newAdapter);
    }

    /// <summary>
    /// FR-057 (Story 2.12): tear down to the ZERO-ADAPTER state, reusing the EXACT FR-050 sequence
    /// (<see cref="SwitchCoreAsync"/> with a <c>null</c> target) rather than forking it. The new scope is
    /// built with <c>preferred: null</c> → <c>AdapterScope.StartAsync</c> re-enumerates, finds zero
    /// eligible adapters, and yields a scope whose <c>CurrentAdapterIPv4</c> is <c>null</c>;
    /// <see cref="StartBoundServicesAsync"/> then starts nothing inbound (NFR-R5). The registry + SSDP log
    /// are still cleared by the shared body, the <c>_switching</c> guard still serialises against a manual
    /// switch, and the app remains running + interactive. A later network change that yields an eligible
    /// adapter rebinds via <see cref="SwitchAdapterAsync"/>.
    /// </summary>
    internal Task SwitchToZeroAdapterAsync() => SwitchCoreAsync(target: null);

    /// <summary>
    /// The shared FR-050 atomic-rebind body. <paramref name="target"/> non-null = a manual switch
    /// (Story 5.2) or a network-change rebind (FR-057) to that adapter; <paramref name="target"/> null =
    /// the FR-057 zero-adapter teardown (build the new scope launch-default, which re-enumerates to an
    /// empty list and binds nothing). Same re-entrancy guard, same registry/log clear, same diagnostics —
    /// the zero-adapter case is just "rebind to nothing", not a separate teardown path.
    /// </summary>
    private async Task SwitchCoreAsync(NetworkAdapter? target)
    {
        // Re-entrancy guard (AC-5.2.9): reject a second switch (or a switch fired during startup). No
        // two scopes ever live at once; no orphaned scope. Shared by the manual switch AND the FR-057
        // auto-rebind (AC #7) — a manual pick in flight rejects the auto call, and vice-versa (NO second
        // guard).
        if (Interlocked.Exchange(ref _switching, 1) == 1)
        {
            _diag.Information(DiagCategories.AdapterSwitch,
                "adapter switch rejected — a switch or startup is already in progress");
            return;
        }

        var oldIp = CurrentAdapterIPv4?.ToString() ?? "(none)";
        var newIp = target?.IPv4.ToString() ?? "(none)";

        // Transient state (NFR-UI3). Pre-await → on the UI thread → safe direct set.
        IsSwitching = true;
        _diag.Information(DiagCategories.AdapterSwitch, "adapter switch started",
            new DiagnosticContext { ErrorText = $"{oldIp} → {newIp}" });

        AdapterScope? newScope = null;
        try
        {
            // ── Steps 1/2/7: cancel the cascade, dispose the transport (2 s budget), dispose _adapterCts.
            //    AdapterScope.DisposeAsync owns these (it owns _adapterCts + the transport).
            if (_adapterScope is not null)
            {
                await _adapterScope.DisposeAsync().ConfigureAwait(false);
                _adapterScope = null;
            }

            // ── Step 3: dispose the OLD callback host (budgeted, idempotent — Story 4.1). The step-1
            //    cancel already unblocked its accept loop + in-flight reads.
            if (_callbackHost is not null)
            {
                await _callbackHost.DisposeAsync().ConfigureAwait(false);
                _callbackHost = null;
            }

            // ── Steps 4/5: drain in-flight fetches. The step-1 _adapterCts.Cancel() already cancelled
            //    every RegistryEntry.DeviceCts (linkage), so each in-flight FetchAsync observes OCE
            //    promptly (AC-7.1 — within 100 ms). There is NO single drainable join handle (fetches are
            //    fire-and-forget off EntryNeedsFetch, bounded only by their device tokens — open-Q #3), so
            //    this is a brief best-effort settle, NOT a hard join (a few yields). We never block the
            //    switch on hung tasks (D7 "don't block UX on hung tasks"); the genuine hung-teardown
            //    timeout path is the transport dispose (step 2), which AdapterScope.DisposeAsync caps at
            //    the budget + emits AdapterSwitchTimeout.
            await DrainInFlightFetchesAsync().ConfigureAwait(false);

            // ── Steps 4/6: clear the registry (raises DeviceRemoved per UUID → tree drops rows + popups
            //    flip to FR-037 device-gone; disposes each DeviceCts) AND clear the SSDP log. Both run on
            //    the UI thread (marshalled): DeviceRegistry.Clear asserts UI thread; SsdpLogViewModel.Clear
            //    is UI-thread-owned. NOT the diagnostic ring sink (app-lifetime — AC-5.2.6).
            await _ui.PostAsync(() =>
            {
                _registry.Clear();
                SsdpLog.Clear();
                return true;
            }).ConfigureAwait(false);

            // ── Steps 8/9/10: build a fresh scope on the chosen adapter, start the bound services
            //    (reusing the same start block as startup), M-SEARCH fires inside scope.StartAsync.
            //    target null (FR-057 zero-adapter teardown) → StartAsync(null) re-enumerates → empty →
            //    CurrentAdapterIPv4 stays null → nothing inbound started (NFR-R5).
            newScope = NewScope();
            _adapterScope = newScope;
            await newScope.StartAsync(target).ConfigureAwait(false);
            await StartBoundServicesAsync(newScope).ConfigureAwait(false);

            var nowOn = newScope.CurrentAdapterIPv4?.ToString() ?? "(no adapter)";
            _diag.Information(DiagCategories.AdapterSwitch, "adapter switch completed",
                new DiagnosticContext { ErrorText = $"now on {nowOn}" });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // AC-5.2.8 / D1 — a failed rebuild must leave a COHERENT, retryable state, never a
            // half-started scope masquerading as the active one. The old scope is already gone (steps
            // 1/2/7), so tear the partial NEW scope down UNCONDITIONALLY (even when it had already been
            // assigned to _adapterScope) and null _adapterScope, so the app is unambiguously "no active
            // adapter — select one to retry": the menu + guard are released in finally, the user
            // re-selects, and the next switch starts cleanly from the null scope. We deliberately do NOT
            // auto-rebind the previous adapter (it was torn down per the D7 atomic sequence; a re-bind
            // could itself fail and is out of scope for v1). The Warning is the surfaced signal (the
            // empty tree + idle discovery are the visible ones; FR-041 Diagnostics viewer shows the why).
            _diag.Warning(DiagCategories.AdapterSwitch, "adapter switch failed — no active adapter; select an adapter to retry",
                new DiagnosticContext { ErrorText = ex.Message });
            if (newScope is not null)
            {
                try { await newScope.DisposeAsync().ConfigureAwait(false); }
                catch (Exception disposeEx) when (disposeEx is not OutOfMemoryException)
                {
                    // Partial-teardown race — tolerated.
                }
            }
            _adapterScope = null;
        }
        finally
        {
            // Clear the transient (post-await → off-thread → marshal) and release the guard.
            _ui.Post(() => IsSwitching = false);
            Interlocked.Exchange(ref _switching, 0);
        }
    }

    /// <summary>
    /// Step 5 best-effort settle window. The fetches were cancelled by step 1; there is no aggregate
    /// join handle (each FetchAsync is fire-and-forget off the registry's EntryNeedsFetch — open-Q #3),
    /// so this is a brief cooperative settle, NOT a hard join: a few yields let cancelled fetch
    /// continuations unwind before the rebuild. It is intentionally short (a fixed handful of yields,
    /// never the FR-050 budget — a budgeted block would itself bloat the switch); the genuine
    /// hung-teardown timeout is the transport dispose (step 2, capped + AdapterSwitchTimeout by
    /// AdapterScope.DisposeAsync). AC-7.1 (OCE within 100 ms) is guaranteed by the token linkage, not by
    /// this window.
    /// </summary>
    private static async Task DrainInFlightFetchesAsync()
    {
        for (var i = 0; i < 3; i++)
        {
            await Task.Yield();
        }
    }

    // ── FR-057 (Story 2.12): host network-change auto-rebind ──────────────────────────────────────────

    /// <summary>
    /// The raw <see cref="INetworkChangeNotifier.NetworkAddressChanged"/> handler. Fires on a NON-UI
    /// thread (Action H). Does the MINIMUM (AC #1): trailing-edge debounce — cancel any in-flight
    /// debounce window and start a fresh one, coalescing the OS notification burst into ONE evaluation
    /// (AC #4). NO enumerate/rebind work happens here.
    /// </summary>
    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        // Atomically swap in a fresh CTS; cancel + dispose the prior pending window (the burst-coalescing
        // reset). Interlocked.Exchange keeps this safe even if the OS fires the event concurrently.
        var fresh = new CancellationTokenSource();
        var prior = Interlocked.Exchange(ref _debounceCts, fresh);
        // Synchronous Cancel: this is a void off-thread event handler (cannot await), and the cancel only
        // trips a CTS the debounce task observes — no blocking teardown runs under it.
#pragma warning disable VSTHRD103
        prior?.Cancel();
#pragma warning restore VSTHRD103
        prior?.Dispose();
        // A26 fire-and-forget; the body swallows OCE + faults. The task handle is retained for the
        // test seam only (production never awaits it — the handler must return promptly off-thread).
        _lastDebounceTask = DebouncedEvaluateAsync(fresh.Token);
    }

    /// <summary>
    /// Waits the debounce window (test-settable seam), then marshals the evaluation onto the UI thread
    /// (Action H — both the event and this continuation are off-thread). A newer event cancels this token
    /// → the await throws OCE → coalesced (AC #4). Any other fault is surfaced as a Warning, never leaked
    /// (A26 fire-and-forget discipline).
    /// </summary>
    private async Task DebouncedEvaluateAsync(CancellationToken token)
    {
        try
        {
            await _networkChangeDebounce(_debounceWindow, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested(); // a newer event may have landed during the delay
            _ui.Post(() => { _lastEvaluateTask = EvaluateNetworkChangeAsync(); });
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer NetworkAddressChanged — coalesced (AC #4). Nothing to do.
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _diag.Warning(DiagCategories.AdapterNetworkChanged, "network-change evaluation failed",
                new DiagnosticContext { ErrorText = ex.Message });
        }
    }

    /// <summary>
    /// Runs on the UI thread (post-marshal). Re-enumerates eligible adapters (FR-048) and decides:
    /// <list type="bullet">
    ///   <item>bound adapter still eligible (same IPv4) → NO-OP (AC #3): no rebind, no diagnostic, no
    ///         <see cref="SwitchAdapterAsync"/> call (so no spurious diagnostic / guard acquisition).</item>
    ///   <item>bound adapter gone / IPv4 changed, an eligible adapter remains → rebind to the first
    ///         eligible (the FR-048 launch-default policy) via <see cref="SwitchAdapterAsync"/> (AC #2).</item>
    ///   <item>zero eligible adapters → tear down to the zero-adapter state via
    ///         <see cref="SwitchToZeroAdapterAsync"/> (AC #5).</item>
    /// </list>
    /// Reading <see cref="CurrentAdapterIPv4"/> AFTER the marshal is deliberate (AC #7): if a manual
    /// switch landed during the debounce, the evaluation sees the new current adapter and correctly
    /// no-ops or rebinds against the new state. Re-entrancy is handled by the shared <c>_switching</c>
    /// guard inside <see cref="SwitchAdapterAsync"/> — no second guard.
    /// </summary>
    private async Task EvaluateNetworkChangeAsync()
    {
        var adapters = _adapterEnum.Enumerate();
        var current = CurrentAdapterIPv4;

        // AC #3 — bound adapter still present + unchanged → no-op. (current null + adapters empty also
        // short-circuits here: there is nothing to rebind to and we are already torn down.)
        var stillBound = current is not null && adapters.Any(a => a.IPv4.Equals(current));
        if (stillBound || (current is null && adapters.Count == 0))
        {
            return;
        }

        var best = adapters.Count > 0 ? adapters[0] : null; // FR-048 launch-default = first eligible
        var oldIp = current?.ToString() ?? "(none)";
        var newIp = best?.IPv4.ToString() ?? "(no eligible adapter)";

        // AC #8 — the network-triggered rebind is distinct from a manual Adapter.Switch / Rescan / Expired.
        _diag.Information(DiagCategories.AdapterNetworkChanged, "network change → auto-rebind",
            new DiagnosticContext { ErrorText = $"{oldIp} → {newIp}" });

        if (best is not null)
        {
            await SwitchAdapterAsync(best).ConfigureAwait(false);   // AC #2
        }
        else
        {
            await SwitchToZeroAdapterAsync().ConfigureAwait(false); // AC #5
        }
    }

    public async ValueTask DisposeAsync()
    {
        // FR-057 (Story 2.12) — detach + cancel BEFORE tearing the scope down so a late network-change
        // event cannot kick a rebind mid-dispose. Unsubscribe the BCL forwarder, cancel any pending
        // debounce window, then dispose the notifier (detaches the BCL static-event handler — the leak
        // guard; a leaked subscriber on NetworkChange roots itself for process life).
        // INVARIANT: the unsubscribe and the CTS exchange below are CONSECUTIVE synchronous statements
        // (no await between them) so the window for a DebouncedEvaluateAsync that already slipped past its
        // token check is as small as possible. An evaluation that still slips through finds _adapterScope
        // null (guarded in SwitchCoreAsync) and no-ops. Do NOT insert an await between these two lines —
        // it would widen that window into a real mid-dispose rebind race.
        _networkChangeNotifier.NetworkAddressChanged -= OnNetworkAddressChanged;
        var pendingDebounce = Interlocked.Exchange(ref _debounceCts, null);
        if (pendingDebounce is not null)
        {
            await pendingDebounce.CancelAsync().ConfigureAwait(false);
            pendingDebounce.Dispose();
        }
        _networkChangeNotifier.Dispose();

        // Await orderly startup completion first so we never dispose the scope mid-bind.
        // RunStartAsync has its own broad catch, so this await never throws.
        if (_runTask is not null)
        {
#pragma warning disable VSTHRD003
            await _runTask.ConfigureAwait(false);
#pragma warning restore VSTHRD003
        }

        if (_adapterScope is not null)
            await _adapterScope.DisposeAsync().ConfigureAwait(false);

        // Story 4.1 — drain the live callback host (budgeted, idempotent). The adapter-token cancel above
        // has already unblocked its accept loop + in-flight reads. Null on the zero-adapter host.
        if (_callbackHost is not null)
            await _callbackHost.DisposeAsync().ConfigureAwait(false);

        // Drain the discovery read loop (its IAsyncDisposable awaits the loop's exit, which the
        // adapter-token cancellation above has already triggered).
        await _discovery.DisposeAsync().ConfigureAwait(false);

        DeviceTree.Dispose();
        SsdpLog.Dispose(); // unsubscribe from AnnouncementReceived
    }
}
