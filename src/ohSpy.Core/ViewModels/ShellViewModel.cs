namespace ohSpy.Core.ViewModels;

using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
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

    public ShellViewModel(
        INetworkAdapterEnumerator adapterEnum,
        Func<ISsdpTransport> transportFactory,
        Func<IEventCallbackHost> callbackHostFactory,
        IDiscoveryService discovery,
        ISubscriptionClient subscriptionClient,
        IDeviceRegistry registry,
        IUiDispatcher ui,
        IDiagnosticEmitter diag,
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
        _deviceTree  = new DeviceTreeViewModel(registry, ui, nodeServices);
        _ssdpLog     = new SsdpLogViewModel(discovery, ui); // subscribes to AnnouncementReceived (app-lifetime)
    }

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
    public async Task SwitchAdapterAsync(NetworkAdapter newAdapter)
    {
        ArgumentNullException.ThrowIfNull(newAdapter);

        // No-op if the chosen adapter is already active (AC-5.2.2). Read before taking the guard.
        if (newAdapter.IPv4.Equals(CurrentAdapterIPv4))
        {
            return;
        }

        // Re-entrancy guard (AC-5.2.9): reject a second switch (or a switch fired during startup). No
        // two scopes ever live at once; no orphaned scope.
        if (Interlocked.Exchange(ref _switching, 1) == 1)
        {
            _diag.Information(DiagCategories.AdapterSwitch,
                "adapter switch rejected — a switch or startup is already in progress");
            return;
        }

        var oldIp = CurrentAdapterIPv4?.ToString() ?? "(none)";
        var newIp = newAdapter.IPv4.ToString();

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
            newScope = NewScope();
            _adapterScope = newScope;
            await newScope.StartAsync(newAdapter).ConfigureAwait(false);
            await StartBoundServicesAsync(newScope).ConfigureAwait(false);

            _diag.Information(DiagCategories.AdapterSwitch, "adapter switch completed",
                new DiagnosticContext { ErrorText = $"now on {newIp}" });
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

    public async ValueTask DisposeAsync()
    {
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
