namespace ohSpy.Core.Discovery;

using System.Net;
using ohSpy.Core.Diagnostics;

/// <summary>
/// The adapter level of the Decision 7 cancellation hierarchy
/// (<c>app → adapter → device → popup</c>). Owns one <see cref="ISsdpTransport"/>,
/// selects the launch-default adapter (FR-048), binds the transport, and issues the
/// startup M-SEARCH (FR-004). Lifetime is bounded by adapter selection — constructed
/// by the app-startup orchestrator (<c>App.OnLaunched</c> now; <c>ShellViewModel</c>
/// in Story 2.5), NOT a DI singleton.
/// <para>
/// This story scaffolds the FR-050 atomic-switch SHAPE only
/// (<see cref="CurrentAdapterIPv4"/> / <see cref="AdapterToken"/> / budgeted
/// <see cref="DisposeAsync"/>). The full switch sequence (Decision 7 steps 3–6:
/// callback-host teardown, per-device CTS cancel, registry clear) is Story 5.2 and
/// references types that do not exist yet.
/// </para>
/// </summary>
internal sealed class AdapterScope : IAsyncDisposable
{
    private static readonly TimeSpan DefaultSwitchBudget = TimeSpan.FromSeconds(2); // FR-050
    private static readonly TimeSpan InitialMx = TimeSpan.FromSeconds(5);           // FR-004

    private readonly INetworkAdapterEnumerator _enumerator;
    private readonly ISsdpTransport _transport;
    private readonly IDiagnosticEmitter _diag;
    private readonly CancellationTokenSource _adapterCts;
    private readonly TimeSpan _switchBudget;
    private volatile bool _transportStarted;
    private int _disposed;

    /// <summary>The bound adapter's IPv4 address, or <c>null</c> when no adapter was selected.</summary>
    public IPAddress? CurrentAdapterIPv4 { get; private set; }

    /// <summary>The adapter-level cancellation token (Decision 7), linked to the app token.</summary>
    public CancellationToken AdapterToken => _adapterCts.Token;

    public AdapterScope(
        INetworkAdapterEnumerator enumerator,
        ISsdpTransport transport,
        IDiagnosticEmitter diag,
        CancellationToken appToken)
        : this(enumerator, transport, diag, DefaultSwitchBudget, appToken)
    {
    }

    /// <summary>
    /// Test seam: identical to the public ctor but with an injectable FR-050 teardown
    /// budget so the budget-exceeded path is testable without a real 2 s wait.
    /// </summary>
    internal AdapterScope(
        INetworkAdapterEnumerator enumerator,
        ISsdpTransport transport,
        IDiagnosticEmitter diag,
        TimeSpan switchBudget,
        CancellationToken appToken)
    {
        _enumerator = enumerator;
        _transport = transport;
        _diag = diag;
        _switchBudget = switchBudget;
        // Decision 7: the adapter level is linked to the app level.
        _adapterCts = CancellationTokenSource.CreateLinkedTokenSource(appToken);
    }

    /// <summary>
    /// Selects the launch-default adapter (FR-048), binds the transport, and issues
    /// the startup M-SEARCH (FR-004). Never throws on the zero-adapter path — the
    /// host still runs (NFR-R5).
    /// </summary>
    public async Task StartAsync()
    {
        var adapters = _enumerator.Enumerate();
        if (adapters.Count == 0)
        {
            // NFR-R5 + FR-048: zero-adapter host still runs. No crash, no dialog.
            _diag.Warning(DiagCategories.AdapterSwitch, "no eligible adapters at startup");
            return;
        }

        var selected = adapters[0]; // FR-048: launch default = first eligible

        await _transport.StartAsync(selected.IPv4, _adapterCts.Token).ConfigureAwait(false);
        // Set after bind succeeds — non-null CurrentAdapterIPv4 implies a live transport.
        CurrentAdapterIPv4 = selected.IPv4;
        _transportStarted = true;
        await _transport.SendMSearchAsync(InitialMx, _adapterCts.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// Cancels the adapter scope and tears down the transport within the FR-050 2 s
    /// budget. Idempotent. Scaffold for the Story 5.2 atomic switch — the full
    /// sequence (callback host, registry) plugs in there.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;

        // 1. Signal the cascade (Decision 7 step 1).
        try
        {
            await _adapterCts.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Already disposed elsewhere — tolerated during teardown.
        }

        // 2. Tear down the transport within the FR-050 2 s budget (Decision 7 step 2).
        //    WaitAsync caps the wait without a dangling timer task; on timeout the
        //    transport's own (swallowing) DisposeAsync continues harmlessly.
        if (_transportStarted)
        {
            try
            {
                await _transport.DisposeAsync().AsTask().WaitAsync(_switchBudget).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _diag.Warning(DiagCategories.AdapterSwitchTimeout, "adapter teardown exceeded budget");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Teardown races are tolerated (same precedent as SsdpTransport.DisposeAsync).
                _diag.Warning(DiagCategories.AdapterSwitch, "adapter teardown error");
            }
        }

        _adapterCts.Dispose();
    }
}
