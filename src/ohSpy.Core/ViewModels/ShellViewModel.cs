namespace ohSpy.Core.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using ohSpy.Core.Devices;
using ohSpy.Core.Diagnostics;
using ohSpy.Core.Discovery;
using ohSpy.Core.Events;
using ohSpy.Core.Threading;

public sealed partial class ShellViewModel : ObservableObject, IAsyncDisposable
{
    private readonly INetworkAdapterEnumerator _adapterEnum;
    private readonly ISsdpTransport _transport;
    private readonly IDiscoveryService _discovery;
    private readonly IEventCallbackHost _callbackHost;
    private readonly ISubscriptionClient _subscriptionClient;
    private readonly IDiagnosticEmitter _diag;

    private AdapterScope? _adapterScope;
    private Task? _runTask;
    private int _started;

    [ObservableProperty]
    private DeviceTreeViewModel _deviceTree;

    [ObservableProperty]
    private SsdpLogViewModel _ssdpLog;

    public ShellViewModel(
        INetworkAdapterEnumerator adapterEnum,
        ISsdpTransport transport,
        IDiscoveryService discovery,
        IEventCallbackHost callbackHost,
        ISubscriptionClient subscriptionClient,
        IDeviceRegistry registry,
        IUiDispatcher ui,
        IDiagnosticEmitter diag,
        NodeServices nodeServices)
    {
        _adapterEnum  = adapterEnum;
        _transport    = transport;
        _discovery    = discovery;
        _callbackHost = callbackHost;
        _subscriptionClient = subscriptionClient;
        _diag         = diag;
        _deviceTree  = new DeviceTreeViewModel(registry, ui, nodeServices);
        _ssdpLog     = new SsdpLogViewModel(discovery, ui); // subscribes to AnnouncementReceived
    }

    // Called from App.OnLaunched (fire-and-forget, Amendment A26 pattern).
    // Constructs and starts the AdapterScope; starts DiscoveryService after scope is live.
    // Single-call: a second invocation is a no-op so it can't orphan the first scope
    // (matches DiscoveryService.StartAsync's Interlocked started-guard precedent).
    public Task StartAsync(CancellationToken appToken)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
            return Task.CompletedTask;

        _adapterScope = new AdapterScope(_adapterEnum, _transport, _diag, appToken);
        // _runTask is retained so DisposeAsync can await orderly startup completion before
        // tearing the scope down (avoids disposing mid-bind). VSTHRD003 is suppressed because
        // _runTask is our own task awaited only from DisposeAsync.
        _runTask = RunStartAsync(_adapterScope);
        return Task.CompletedTask;
    }

    private async Task RunStartAsync(AdapterScope scope)
    {
        try
        {
            await scope.StartAsync().ConfigureAwait(false);
            if (scope.CurrentAdapterIPv4 is not null)
            {
                // Story 4.1 — start the GENA callback host on the bound adapter IP (the first point
                // the IP is known), bounded by the adapter token, before discovery. It binds a
                // TcpListener on (adapterIPv4, ephemeral) — NOT 0.0.0.0 — so SUBSCRIBE (Story 4.2)
                // can announce CallbackBaseUrl. Lifecycle is owned here (disposed in DisposeAsync).
                await _callbackHost.StartAsync(scope.CurrentAdapterIPv4, scope.AdapterToken)
                                   .ConfigureAwait(false);

                // Story 4.2 — hand the per-AdapterScope token to the subscription client (the DI
                // singleton can't inject it). This is the D7 "level above" used for the UNSUBSCRIBE-on-
                // active-close + the adapter-switch lapse cascade. Story 5.2's atomic rebind re-calls
                // this on the new adapter's token (and the adapter-token cancel lapses all live subs).
                _subscriptionClient.SetAdapterContext(scope.AdapterToken);

                await _discovery.StartAsync(scope.AdapterToken, scope.AdapterToken)
                                .ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _diag.Warning(DiagCategories.AdapterSwitch,
                "adapter startup failed — no SSDP traffic",
                new DiagnosticContext { ErrorText = ex.Message });
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

        // Story 4.1 — drain the callback host (budgeted, idempotent). The adapter-token cancel above
        // has already unblocked its accept loop + in-flight reads; DisposeAsync drains within 2 s.
        // Started only when CurrentAdapterIPv4 was non-null; DisposeAsync is a safe no-op otherwise.
        await _callbackHost.DisposeAsync().ConfigureAwait(false);

        // Drain the discovery read loop (its IAsyncDisposable awaits the loop's exit, which
        // the adapter-token cancellation above has already triggered). ShellViewModel started
        // it, so ShellViewModel drains it (the DI container never disposes this singleton).
        await _discovery.DisposeAsync().ConfigureAwait(false);

        DeviceTree.Dispose();
        SsdpLog.Dispose(); // unsubscribe from AnnouncementReceived
    }
}
