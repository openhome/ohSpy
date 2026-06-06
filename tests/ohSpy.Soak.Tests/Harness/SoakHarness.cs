namespace ohSpy.Soak.Tests.Harness;

using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ohSpy.Core.Devices;
using ohSpy.Core.Diagnostics;
using ohSpy.Core.Discovery;
using ohSpy.Core.Events;
using ohSpy.Core.Http;
using ohSpy.Core.Models;
using ohSpy.Core.Scpd;
using ohSpy.Core.ViewModels;
using ohSpy.Soak.Tests.Fakes;
using ohSpy.Soak.Tests.Farm;

/// <summary>
/// Story 6.2 Task 3 — the shared headless soak harness. Assembles the REAL Core VM + service stack
/// (mirroring <c>ShellViewModelTests.NewHarness</c>) and drives it against a <see cref="DeviceFarm"/>:
/// <list type="bullet">
///   <item>real <see cref="ShellViewModel"/> + <see cref="DeviceRegistry"/> + <see cref="DiscoveryService"/>
///         + <see cref="SubscriptionClient"/> + real <see cref="EventCallbackHost"/> (factory) +
///         <see cref="EagerDescriptionDispatcher"/> + the REAL <see cref="DiagnosticEmitter"/> fan-out
///         (ring + file + gate);</item>
///   <item>the <see cref="PumpingUiDispatcher"/> as <see cref="ohSpy.Core.Threading.IUiDispatcher"/> so
///         marshalling is genuinely exercised and UI-stalls are measured (⭐#1);</item>
///   <item>the REAL <see cref="UpnpHttpClient"/> hitting the farm's loopback HTTP endpoints
///         (description / SCPD / SOAP / GENA SUBSCRIBE+NOTIFY);</item>
///   <item>the REAL <see cref="DiagnosticFileSink"/> via its internal temp-dir ctor so on-disk log
///         rollover is exercised over the (compressed) run (⭐#6).</item>
/// </list>
/// The farm's SSDP is injected through the scope-owned <see cref="FarmSsdpTransport"/> (the writable
/// transport the factory hands out). NEVER references WinUI.
/// </summary>
internal sealed class SoakHarness : IAsyncDisposable
{
    // A single loopback adapter surrogate; a second adapter exists so SwitchAdapterAsync has somewhere
    // to switch to. Both bind 127.0.0.1 (the farm + callback host all live on loopback).
    private static readonly NetworkAdapter AdapterA =
        new("SoakLoopbackA", "Soak loopback A", IPAddress.Loopback);
    private static readonly NetworkAdapter AdapterB =
        new("SoakLoopbackB", "Soak loopback B", IPAddress.Loopback);

    private readonly PumpingUiDispatcher _ui;
    private readonly UnhandledExceptionCapture _exceptions;
    private readonly DiagnosticEmitter _emitter;
    private readonly DiagnosticRingSink _ringSink;
    private readonly DiagnosticFileSink _fileSink;
    private readonly DiagnosticLevelGate _gate;
    private readonly DeviceRegistry _registry;
    private readonly DiscoveryService _discovery;
    private readonly SubscriptionClient _subscriptionClient;
    private readonly UpnpHttpClient _http;
    private readonly EagerDescriptionDispatcher _eagerDispatcher;
    private readonly SsdpLogViewModel _ssdpLog;
    private readonly DiagnosticsViewModel _diagnostics;
    private readonly DeviceFarm _farm;
    private readonly FarmSsdpTransport _transport;
    private readonly List<IEventCallbackHost> _callbackHosts = new();
    private readonly List<SubscriptionPopupViewModel> _openPopups = new();
    private bool _adapterToggle;

    public string DiagnosticsTempDir { get; }

    /// <summary>The sustained SSDP advertise rate the farm runs at (for the report).</summary>
    public int AdvertsPerSecond { get; }

    public PumpingUiDispatcher Ui => _ui;
    public UnhandledExceptionCapture Exceptions => _exceptions;
    public ShellViewModel Shell { get; }
    public DeviceFarm Farm => _farm;
    public DeviceRegistry Registry => _registry;
    public SsdpLogViewModel SsdpLog => _ssdpLog;
    public DiagnosticsViewModel Diagnostics => _diagnostics;
    public DiagnosticRingSink RingSink => _ringSink;
    public IReadOnlyList<SubscriptionPopupViewModel> OpenPopups => _openPopups;

    public IEventCallbackHost? CurrentCallbackHost =>
        _callbackHosts.Count > 0 ? _callbackHosts[^1] : null;

    /// <summary>Story 6.3 — the farm's 120-action GiantScpd device (set when built with misbehaving on),
    /// for the cold-large-SCPD reproducer. Its UDN maps to a device node in <see cref="Shell"/>'s tree.</summary>
    public FarmUpnpDevice? GiantScpdDevice => _farm.GiantScpdDevice;

    public SoakHarness(int advertsPerSecond)
    {
        AdvertsPerSecond = advertsPerSecond;
        _ui = new PumpingUiDispatcher();
        _exceptions = new UnhandledExceptionCapture();
        _ui.UiThreadException += _exceptions.Record;

        // ── Real diagnostic pipeline (ring + file temp-dir + gate + fan-out emitter) ──
        var diagOptions = Options.Create(new DiagnosticOptions { MinSeverity = DiagSeverity.Information });
        _gate = new DiagnosticLevelGate(diagOptions);
        _ringSink = new DiagnosticRingSink(_ui, new SoakIdentityLookup());

        DiagnosticsTempDir = Path.Combine(
            Path.GetTempPath(), "ohSpy-soak", $"diag-{Guid.NewGuid():N}");
        _fileSink = new DiagnosticFileSink(NullLogger<DiagnosticFileSink>.Instance, DiagnosticsTempDir);

        _emitter = new DiagnosticEmitter(NullLogger<DiagnosticEmitter>.Instance, _ringSink, _fileSink, _gate);

        // ── Real HTTP client (hits the farm's loopback endpoints) ──
        var httpOptions = Options.Create(new HttpTimeoutOptions());
        _http = new UpnpHttpClient(httpOptions, _emitter);

        // ── Real discovery + registry + eager-description dispatcher ──
        _registry = new DeviceRegistry(_ui);
        var descParser = new DeviceDescriptionParser();
        var scpdParser = new XmlReaderScpdParser();
        _eagerDispatcher = new EagerDescriptionDispatcher(_http, descParser, _ui, _registry, _emitter);

        var ssdpParser = new SsdpParser(_emitter);
        _discovery = new DiscoveryService(_registry, ssdpParser, _ui);

        // ── Real subscription client + callback-host factory ──
        _subscriptionClient = new SubscriptionClient(_http, _emitter);
        Func<IEventCallbackHost> hostFactory = () =>
        {
            var host = new EventCallbackHost(httpOptions, _emitter);
            _callbackHosts.Add(host);
            return host;
        };

        // ── The farm + its writable transport (the factory hands the SAME instance to the scope) ──
        _transport = new FarmSsdpTransport();
        Func<ISsdpTransport> transportFactory = () => _transport;
        _farm = new DeviceFarm(_transport, advertsPerSecond);

        var enumerator = new SoakAdapterEnumerator(AdapterA, AdapterB);

        var nodeServices = new NodeServices(
            _http, scpdParser, _ui, _emitter,
            new NoOpUriLauncher(), new NoOpPropertiesLauncher(),
            new NoOpInvocationPopupLauncher(), new NoOpSubscriptionPopupLauncher());

        Shell = new ShellViewModel(
            enumerator, transportFactory, hostFactory,
            _discovery, _subscriptionClient, _registry, _ui, _emitter,
            new NoOpDiagnosticsLauncher(), nodeServices);

        _ssdpLog = Shell.SsdpLog; // the app-lifetime SSDP log (subscribes to AnnouncementReceived)
        _diagnostics = new DiagnosticsViewModel(_ringSink, _gate);

        // Compress the rescan MX wait so a rescan does not really sleep MX (5 s) during the run.
        Shell.SetRescanDelayForTest((d, ct) => Task.Delay(TimeSpan.FromMilliseconds(50), ct));
    }

    /// <summary>Build the farm, start the Core, begin the advertiser + tick instrumentation.</summary>
    public async Task StartAsync(int normalDevices, bool includeMisbehaving, CancellationToken ct)
    {
        await _farm.BuildAsync(normalDevices, includeMisbehaving, ct).ConfigureAwait(false);

        await Shell.StartAsync(CancellationToken.None).ConfigureAwait(false);
        await Shell.WaitForStartupAsync().ConfigureAwait(false);

        _farm.StartAdvertiser();
        // Initial burst so devices populate the tree promptly.
        await _farm.BurstAliveAsync(ct).ConfigureAwait(false);

        _ui.StartTicking();
    }

    /// <summary>Wait until at least <paramref name="minDevices"/> rows have loaded into the registry,
    /// or the budget elapses (devices fetch their description over loopback HTTP).</summary>
    public async Task WaitForDevicesAsync(int minDevices, TimeSpan budget, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + budget;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            if (_registry.Loaded.Count >= minDevices)
            {
                return;
            }
            await Task.Delay(50, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Open a subscription popup on the first service of <paramref name="entry"/> and run its
    /// real SUBSCRIBE through the live callback host. Tracks it for the closable-popup assertion.</summary>
    public async Task<SubscriptionPopupViewModel?> OpenSubscriptionPopupAsync(RegistryEntry entry)
    {
        var services = entry.Description?.Services;
        if (services is null || services.Count == 0)
        {
            return null;
        }
        var service = services[0];
        var popup = new SubscriptionPopupViewModel(
            service, entry, _subscriptionClient, _ui, _emitter, _registry);
        _openPopups.Add(popup);
        await popup.InitializeAsync().ConfigureAwait(false);
        return popup;
    }

    /// <summary>Construct + initialise an invocation popup for a service (the SOAP step). The action is a
    /// trivial argument-less action so the popup can submit a real SOAP POST over loopback.</summary>
    public InvocationPopupViewModel BuildInvocationPopup(ServiceDescription service, RegistryEntry entry)
    {
        var action = new ScpdAction("Ping", Array.Empty<ScpdArgument>(), Array.Empty<ScpdArgument>());
        return new InvocationPopupViewModel(
            action, service, entry, _http, _ui, _emitter, _registry,
            new XmlReaderScpdParser());
    }

    /// <summary>Open + close a Properties popup (the "open/close diagnostic viewer"-adjacent step uses
    /// the DiagnosticsViewModel directly; this drives the device Properties VM for full coverage).</summary>
    public PropertiesViewModel BuildPropertiesPopup(RegistryEntry entry) =>
        new(entry, _registry, new NoOpUriLauncher(), _emitter);

    /// <summary>Switch the adapter once (alternates A/B). Both are loopback so the farm keeps working.</summary>
    public Task SwitchAdapterAsync()
    {
        _adapterToggle = !_adapterToggle;
        return Shell.SwitchAdapterAsync(_adapterToggle ? AdapterB : AdapterA);
    }

    public Task RescanAsync()
    {
        Shell.RescanCommand.Execute(null);
        return Task.CompletedTask;
    }

    /// <summary>Dispose every still-open popup and assert each cleared its disposed guard cleanly
    /// (AC-6.2.5 closable). Throws nothing; a popup that faults on dispose records to the capture.</summary>
    public void CloseAllPopups()
    {
        foreach (var popup in _openPopups)
        {
            try { popup.Dispose(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { _exceptions.Record(ex); }
        }
    }

    /// <summary>Flush the on-disk diagnostic file sink so its rollover state is observable BEFORE the
    /// run's assertions (the sink drains its channel with a 5 s budget). Idempotent enough for the soak.</summary>
    public Task FlushDiagnosticsAsync() => _fileSink.FlushAsync(CancellationToken.None);

    public async ValueTask DisposeAsync()
    {
        CloseAllPopups();
        await _farm.DisposeAsync().ConfigureAwait(false);
        await Shell.DisposeAsync().ConfigureAwait(false);
        await _discovery.DisposeAsync().ConfigureAwait(false);
        await _fileSink.DisposeAsync().ConfigureAwait(false);
        _eagerDispatcher.Dispose();
        _ssdpLog.Dispose();
        // Note: SubscriptionClient has no DisposeAsync (deferred-work.md); the adapter-token cancel on
        // Shell.DisposeAsync already cascades into every live subscription's renew loop + worker.
        _http.Dispose();
        _exceptions.Dispose();
        _ui.Dispose();

        // Best-effort temp-dir cleanup (leave it on failure for post-mortem).
        try
        {
            if (Directory.Exists(DiagnosticsTempDir))
            {
                Directory.Delete(DiagnosticsTempDir, recursive: true);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { /* tolerate locked files */ }
    }
}

/// <summary>Soak-scoped adapter enumerator returning a fixed list (≥ 2 so SwitchAdapterAsync works).</summary>
internal sealed class SoakAdapterEnumerator(params NetworkAdapter[] adapters) : INetworkAdapterEnumerator
{
    public IReadOnlyList<NetworkAdapter> Enumerate() => adapters;
}
