namespace ohSpy.Core.Discovery;

using System.Net;
using System.Threading.Channels;
using ohSpy.Core.Diagnostics;
using ohSpy.Core.Models;

/// <summary>
/// The adapter level of the Decision 7 cancellation hierarchy
/// (<c>app → adapter → device → popup</c>). Owns one <see cref="ISsdpTransport"/>,
/// binds the chosen adapter (FR-048), and issues the startup M-SEARCH (FR-004).
/// Lifetime is bounded by adapter selection — constructed by <c>ShellViewModel</c>
/// (Story 2.5), NOT a DI singleton.
/// <para>
/// Amendment A23 (Story 5.2): the transport is no longer a DI singleton — the scope
/// constructs and OWNS it via the injected <see cref="Func{ISsdpTransport}"/> factory
/// (Pattern 7, matching the popup-VM factories), and disposes it on teardown. The
/// scope EXPOSES the live transport's <see cref="IncomingDatagrams"/> so the singleton
/// <c>DiscoveryService</c> reads the SCOPE-OWNED instance (never a second DI-resolved
/// one). A fresh scope ⇒ a fresh transport bound to the new adapter — the atomic switch.
/// </para>
/// <para>
/// The FR-050 atomic-switch sequence is owned by <c>ShellViewModel.SwitchAdapterAsync</c>;
/// this scope's <see cref="DisposeAsync"/> performs Decision 7 steps 1 (cancel), 2
/// (transport dispose within the 2 s budget) and 7 (dispose the adapter CTS). Steps 3–6
/// (callback host, fetch drain, registry/log clear) and 8–10 (rebuild) live in
/// <c>ShellViewModel</c> because the callback host + registry + log are owned there.
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

    /// <summary>
    /// The scope-owned transport's datagram reader (A23). <c>DiscoveryService</c> reads
    /// THIS reader (passed via <c>StartAsync</c>/<c>RebindAsync</c>) — never a separately
    /// DI-resolved transport. Throws if accessed before the transport <see cref="StartAsync"/>.
    /// </summary>
    public ChannelReader<SsdpDatagram> IncomingDatagrams => _transport.IncomingDatagrams;

    public AdapterScope(
        INetworkAdapterEnumerator enumerator,
        Func<ISsdpTransport> transportFactory,
        IDiagnosticEmitter diag,
        CancellationToken appToken)
        : this(enumerator, transportFactory, diag, DefaultSwitchBudget, appToken)
    {
    }

    /// <summary>
    /// Test seam: identical to the public ctor but with an injectable FR-050 teardown
    /// budget so the budget-exceeded path is testable without a real 2 s wait.
    /// </summary>
    internal AdapterScope(
        INetworkAdapterEnumerator enumerator,
        Func<ISsdpTransport> transportFactory,
        IDiagnosticEmitter diag,
        TimeSpan switchBudget,
        CancellationToken appToken)
    {
        ArgumentNullException.ThrowIfNull(transportFactory);
        _enumerator = enumerator;
        // A23: construct + OWN the transport here (one per scope, disposed on teardown).
        _transport = transportFactory();
        _diag = diag;
        _switchBudget = switchBudget;
        // Decision 7: the adapter level is linked to the app level.
        _adapterCts = CancellationTokenSource.CreateLinkedTokenSource(appToken);
    }

    /// <summary>
    /// Binds the chosen adapter (FR-048) and issues the startup M-SEARCH (FR-004). When
    /// <paramref name="preferred"/> is <c>null</c> the launch default (the first eligible
    /// adapter) is selected; when supplied (the Story 5.2 switch) that adapter is bound.
    /// Never throws on the zero-adapter path — the host still runs (NFR-R5).
    /// </summary>
    public async Task StartAsync(NetworkAdapter? preferred = null)
    {
        NetworkAdapter? selected;
        if (preferred is not null)
        {
            // Story 5.2 switch: bind the operator-chosen adapter directly (no re-enumeration
            // needed — the menu already enumerated; the chosen record carries the IPv4).
            selected = preferred;
        }
        else
        {
            var adapters = _enumerator.Enumerate();
            if (adapters.Count == 0)
            {
                // NFR-R5 + FR-048: zero-adapter host still runs. No crash, no dialog.
                _diag.Warning(DiagCategories.AdapterSwitch, "no eligible adapters at startup");
                return;
            }

            selected = adapters[0]; // FR-048: launch default = first eligible
        }

        await _transport.StartAsync(selected.IPv4, _adapterCts.Token).ConfigureAwait(false);
        // Set after bind succeeds — non-null CurrentAdapterIPv4 implies a live transport.
        CurrentAdapterIPv4 = selected.IPv4;
        _transportStarted = true;
        await _transport.SendMSearchAsync(InitialMx, _adapterCts.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// Story 5.3 rescan re-trigger (FR-022). Re-issues an M-SEARCH (<c>ST: upnp:rootdevice</c>, the
    /// identical wire semantics as the startup search at <see cref="StartAsync"/>) on the bound adapter
    /// via the SCOPE-OWNED transport, using the scope's own <c>_adapterCts</c> token — so a concurrent
    /// adapter switch (which cancels that token in <see cref="DisposeAsync"/>) aborts an in-flight rescan
    /// (AC-5.3.10 "switch wins"). A23 keeps the transport encapsulated here; the orchestration lives in
    /// <c>ShellViewModel.RescanAsync</c>. Defensive no-op before a successful <see cref="StartAsync"/>
    /// (the zero-adapter scope has no live transport).
    /// </summary>
    public Task SendMSearchAsync(TimeSpan mx)
    {
        if (!_transportStarted)
        {
            return Task.CompletedTask; // zero-adapter / not-yet-bound scope: nothing to scan (NFR-R5)
        }

        try
        {
            return _transport.SendMSearchAsync(mx, _adapterCts.Token);
        }
        catch (ObjectDisposedException)
        {
            // Switch-wins race (AC-5.3.10): DisposeAsync cancelled THEN disposed _adapterCts between the
            // caller's scope check and this token read, so `_adapterCts.Token` throws ODE. Surface it as
            // cancellation — the caller's "rescan abandoned, switch in progress" path — not a generic
            // failure (which would log a misleading "rescan failed" with an ODE message).
            throw new OperationCanceledException("adapter scope disposed during rescan");
        }
    }

    /// <summary>
    /// Cancels the adapter scope and tears down the transport within the FR-050 2 s
    /// budget (Decision 7 steps 1 / 2 / 7). Idempotent. The rest of the atomic switch
    /// (callback host, registry/log clear, rebuild) is orchestrated by
    /// <c>ShellViewModel.SwitchAdapterAsync</c> around this dispose.
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
        else
        {
            // Even an unstarted transport must be disposed (the factory always constructs one).
            try { await _transport.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { /* nothing bound — tolerated */ }
        }

        _adapterCts.Dispose();
    }
}
