namespace ohSpy.Core.Tests.Discovery;

using FluentAssertions;
using ohSpy.Core.Diagnostics;
using ohSpy.Core.Devices;
using ohSpy.Core.Discovery;
using ohSpy.Core.Tests.Fakes;
using Xunit;

public sealed class DiscoveryServiceTests
{
    // Amendment A30: identity is the UDN string. The "body" is the USN token after "uuid:";
    // the registry UDN is "uuid:{body}".
    private const string RootBody = "aaaaaaaa-0000-0000-0000-000000000001";
    private const string AnotherBody = "bbbbbbbb-0000-0000-0000-000000000002";
    private const string RootUdn = "uuid:" + RootBody;
    private const string AnotherUdn = "uuid:" + AnotherBody;

    private static (DiscoveryService service, ChannelSsdpTransport transport,
        DeviceRegistry registry, CapturingDiagnosticEmitter cap)
        MakeSystem()
    {
        var cap = new CapturingDiagnosticEmitter();
        var ui = new InlineUiDispatcher();
        var transport = new ChannelSsdpTransport();
        var registry = new DeviceRegistry(ui);
        var parser = new SsdpParser(cap);
        var service = new DiscoveryService(registry, parser, ui, cap);
        return (service, transport, registry, cap);
    }

    // Helper: write datagrams, complete channel, drain by awaiting DisposeAsync.
    private static async Task DrainAsync(DiscoveryService service, ChannelSsdpTransport transport)
    {
        transport.Complete();
        await service.DisposeAsync();
    }

    [Fact]
    [Trait("ac", "AC-2.4.5")]
    public async Task StartAsync_Alive_RootUuid_AddsEntryToRegistry_AC245()
    {
        var (service, transport, registry, _) = MakeSystem();
        var fetchFired = 0;
        registry.EntryNeedsFetch += _ => fetchFired++;

        await service.StartAsync(transport.IncomingDatagrams, CancellationToken.None, CancellationToken.None);
        await transport.WriteAsync(SsdpDatagramBuilder.Notify("upnp:rootdevice", "ssdp:alive", RootBody));
        await DrainAsync(service, transport);

        registry.Count.Should().Be(1);
        fetchFired.Should().Be(1);
    }

    [Fact]
    [Trait("ac", "AC-2.4.6")]
    public async Task StartAsync_Alive_KnownUuid_RefreshesNoNewEntry_AC246()
    {
        var (service, transport, registry, _) = MakeSystem();

        await service.StartAsync(transport.IncomingDatagrams, CancellationToken.None, CancellationToken.None);
        await transport.WriteAsync(SsdpDatagramBuilder.Notify("upnp:rootdevice", "ssdp:alive", RootBody));
        transport.Complete();
        await service.DisposeAsync();

        // Second pass with a fresh service (same registry)
        var transport2 = new ChannelSsdpTransport();
        var cap2 = new CapturingDiagnosticEmitter();
        var ui2 = new InlineUiDispatcher();
        var parser2 = new SsdpParser(cap2);
        var service2 = new DiscoveryService(registry, parser2, ui2, cap2);

        await service2.StartAsync(transport2.IncomingDatagrams, CancellationToken.None, CancellationToken.None);
        await transport2.WriteAsync(SsdpDatagramBuilder.Notify("upnp:rootdevice", "ssdp:alive", RootBody));
        await DrainAsync(service2, transport2);

        registry.Count.Should().Be(1);
        registry.TryGetEntry(RootUdn, out var entry).Should().BeTrue();
        entry.AliveCount.Should().Be(2);
    }

    [Fact]
    [Trait("ac", "AC-2.4.7")]
    public async Task StartAsync_Byebye_KnownUuid_RemovesEntry_AC247()
    {
        var (service, transport, registry, _) = MakeSystem();
        string? removedUdn = null;
        registry.DeviceRemoved += id => removedUdn = id;

        await service.StartAsync(transport.IncomingDatagrams, CancellationToken.None, CancellationToken.None);
        await transport.WriteAsync(SsdpDatagramBuilder.Notify("upnp:rootdevice", "ssdp:alive", RootBody));
        await transport.WriteAsync(SsdpDatagramBuilder.Notify("upnp:rootdevice", "ssdp:byebye", RootBody));
        await DrainAsync(service, transport);

        registry.Count.Should().Be(0);
        removedUdn.Should().Be(RootUdn);
    }

    [Fact]
    [Trait("ac", "AC-2.4.7")]
    public async Task StartAsync_Byebye_UnknownUuid_RegistryUnchanged_AC247()
    {
        var (service, transport, registry, _) = MakeSystem();

        await service.StartAsync(transport.IncomingDatagrams, CancellationToken.None, CancellationToken.None);
        await transport.WriteAsync(SsdpDatagramBuilder.Notify("upnp:rootdevice", "ssdp:byebye", AnotherBody));
        await DrainAsync(service, transport);

        registry.Count.Should().Be(0);
    }

    [Fact]
    [Trait("ac", "AC-2.4.8")]
    public async Task StartAsync_EmbeddedDevice_RegistryMuted_AnnouncementRaised_AC248()
    {
        var (service, transport, registry, _) = MakeSystem();
        var announced = 0;
        service.AnnouncementReceived += _ => announced++;

        await service.StartAsync(transport.IncomingDatagrams, CancellationToken.None, CancellationToken.None);
        await transport.WriteAsync(SsdpDatagramBuilder.Notify(
            "urn:schemas-upnp-org:device:MediaRenderer:1", "ssdp:alive", RootBody));
        await DrainAsync(service, transport);

        registry.Count.Should().Be(0);
        announced.Should().Be(1);
    }

    [Fact]
    [Trait("ac", "AC-2.4.10")]
    public async Task StartAsync_MSearchResponse_TreatedAsAlive_AC2410()
    {
        var (service, transport, registry, _) = MakeSystem();

        await service.StartAsync(transport.IncomingDatagrams, CancellationToken.None, CancellationToken.None);
        await transport.WriteAsync(SsdpDatagramBuilder.SearchResponse(RootBody));
        await DrainAsync(service, transport);

        registry.Count.Should().Be(1);
    }

    [Fact]
    [Trait("ac", "AC-2.4.3")]
    public async Task StartAsync_Malformed_EmitsWarning_RegistryUnchanged_AC243()
    {
        var (service, transport, registry, cap) = MakeSystem();
        var announced = 0;
        service.AnnouncementReceived += _ => announced++;

        await service.StartAsync(transport.IncomingDatagrams, CancellationToken.None, CancellationToken.None);
        await transport.WriteAsync(SsdpDatagramBuilder.Malformed());
        await DrainAsync(service, transport);

        registry.Count.Should().Be(0);
        announced.Should().Be(0);
        cap.Entries.Should().ContainSingle(e =>
            e.Severity == "Warning" && e.Category == DiagCategories.SsdpParse);
    }

    [Fact]
    [Trait("ac", "AC-2.4.4")]
    public async Task StartAsync_CancelToken_LoopExitsCleanly_AC244()
    {
        var (service, transport, _registry, _) = MakeSystem();
        using var cts = new CancellationTokenSource();

        await service.StartAsync(transport.IncomingDatagrams, cts.Token, CancellationToken.None);
        await transport.WriteAsync(SsdpDatagramBuilder.Notify("upnp:rootdevice", "ssdp:alive", RootBody));
        await cts.CancelAsync();

        var disposeTask = service.DisposeAsync().AsTask();
        var completed = await Task.WhenAny(disposeTask, Task.Delay(200));
        completed.Should().Be(disposeTask, "DisposeAsync should complete within 200 ms after cancellation");
    }

    [Fact]
    [Trait("ac", "AC-2.4.9")]
    public async Task AnnouncementReceived_FiredForAllParsedAnnouncements_AC249()
    {
        var (service, transport, _registry, _) = MakeSystem();
        var announcements = new List<SsdpAnnouncement>();
        service.AnnouncementReceived += a => announcements.Add(a);

        await service.StartAsync(transport.IncomingDatagrams, CancellationToken.None, CancellationToken.None);
        // root alive
        await transport.WriteAsync(SsdpDatagramBuilder.Notify("upnp:rootdevice", "ssdp:alive", RootBody));
        // embedded alive
        await transport.WriteAsync(SsdpDatagramBuilder.Notify(
            "urn:schemas-upnp-org:device:MediaRenderer:1", "ssdp:alive", AnotherBody));
        // root byebye
        await transport.WriteAsync(SsdpDatagramBuilder.Notify("upnp:rootdevice", "ssdp:byebye", RootBody));
        // malformed — should NOT raise event
        await transport.WriteAsync(SsdpDatagramBuilder.Malformed());
        await DrainAsync(service, transport);

        announcements.Should().HaveCount(3, "malformed datagram must not raise AnnouncementReceived");
    }

    // ─── Amendment A30 regression (e): a non-GUID-UDN root alive routes into OnAlive ─────────────
    // Pre-A30 the Uuid.HasValue gate dropped a non-RFC-4122 UDN; the gate is now !IsNullOrEmpty(Udn).

    [Fact]
    [Trait("ac", "AC-2.4.1")]
    public async Task StartAsync_Alive_NonGuidUdn_RoutesIntoOnAlive()
    {
        var (service, transport, registry, _) = MakeSystem();
        const string linnBody = "linn-ds-0001"; // non-RFC-4122 — would have been dropped pre-A30

        await service.StartAsync(transport.IncomingDatagrams, CancellationToken.None, CancellationToken.None);
        await transport.WriteAsync(SsdpDatagramBuilder.Notify("upnp:rootdevice", "ssdp:alive", linnBody));
        await DrainAsync(service, transport);

        registry.Count.Should().Be(1, "the non-GUID alive reached OnAlive (the old HasValue gate is gone)");
        registry.TryGetEntry("uuid:" + linnBody, out _).Should().BeTrue();
    }

    [Fact]
    [Trait("ac", "AC-2.4.1")]
    public async Task StartAsync_Byebye_NonGuidUdn_RoutesIntoOnByebye()
    {
        var (service, transport, registry, _) = MakeSystem();
        const string linnBody = "linn-ds-0001";
        string? removedUdn = null;
        registry.DeviceRemoved += id => removedUdn = id;

        await service.StartAsync(transport.IncomingDatagrams, CancellationToken.None, CancellationToken.None);
        await transport.WriteAsync(SsdpDatagramBuilder.Notify("upnp:rootdevice", "ssdp:alive", linnBody));
        await transport.WriteAsync(SsdpDatagramBuilder.Notify("upnp:rootdevice", "ssdp:byebye", linnBody));
        await DrainAsync(service, transport);

        registry.Count.Should().Be(0);
        removedUdn.Should().Be("uuid:" + linnBody);
    }

    // ─── Story 2.11 / FR-056: the periodic expiry sweep (inferred byebye) ────────────────────────────
    //
    // The sweep loop is driven via the injected clock + delay seam (instant — no real waits). The
    // `_delay` is a OneShotDelay: it completes on the FIRST call (so exactly one sweep cycle runs) then
    // blocks until the loop's linked token cancels (teardown) — guaranteeing a single, observable sweep.

    /// <summary>
    /// A delay seam that releases the FIRST await immediately, then parks every subsequent await on a
    /// task that only completes when the loop's token cancels — so the sweep loop runs exactly one cycle
    /// and then idles until teardown. <see cref="FirstAwaited"/> completes once the loop has consumed
    /// the first delay, letting the test await the single sweep deterministically.
    /// </summary>
    private sealed class OneShotDelay
    {
        private int _calls;
        private readonly TaskCompletionSource _firstAwaited =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task FirstAwaited => _firstAwaited.Task;

        public Task InvokeAsync(TimeSpan _, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                _firstAwaited.TrySetResult();
                return Task.CompletedTask; // release the first cycle immediately
            }

            // Park subsequent cycles until the loop's token cancels (normal shutdown).
            return Task.Delay(Timeout.Infinite, ct);
        }
    }

    // Drain helper for the sweep tests: the sweep loop parks on its linked token, so we cancel the
    // adapter token FIRST (its normal stop signal), then complete the channel + await DisposeAsync.
    private static async Task DrainSweepAsync(DiscoveryService service, ChannelSsdpTransport transport,
        CancellationTokenSource adapterCts)
    {
        await adapterCts.CancelAsync();
        transport.Complete();
        await service.DisposeAsync();
    }

    [Fact]
    [Trait("fr", "FR-056")]
    public async Task Sweep_EvictsStaleEntry_EmitsSsdpExpired_FR056()
    {
        var (service, transport, registry, cap) = MakeSystem();
        using var adapterCts = new CancellationTokenSource();
        string? removedUdn = null;
        registry.DeviceRemoved += id => removedUdn = id;

        var delay = new OneShotDelay();
        service.SetSweepDelayForTest(delay.InvokeAsync);
        // Clock far past the 1800s lease the datagram builder advertises (+ jitter).
        service.SetClockForTest(() => DateTime.UtcNow + TimeSpan.FromSeconds(2000));

        await service.StartAsync(transport.IncomingDatagrams, adapterCts.Token, CancellationToken.None);
        await transport.WriteAsync(SsdpDatagramBuilder.Notify("upnp:rootdevice", "ssdp:alive", RootBody));

        // Let the alive route into the registry first (the read loop posts it via the inline dispatcher).
        await WaitUntilAsync(() => registry.Count == 1);

        // Wait for the single sweep cycle to execute (the inline dispatcher runs ExpireOlderThan inline).
        await delay.FirstAwaited;
        await WaitUntilAsync(() => registry.Count == 0);

        await DrainSweepAsync(service, transport, adapterCts);

        registry.Count.Should().Be(0, "the stale entry was evicted by the sweep");
        removedUdn.Should().Be(RootUdn, "the sweep eviction uses the byebye-identical RemoveCore cascade");
        cap.Entries.Should().ContainSingle(e =>
            e.Severity == "Information" &&
            e.Category == DiagCategories.SsdpExpired &&
            e.Context.DeviceUuid == RootUdn,
            "one Ssdp.Expired Information diagnostic per evicted UDN");
    }

    [Fact]
    [Trait("fr", "FR-056")]
    public async Task Sweep_LiveEntryWithinLease_NotEvicted_FR056()
    {
        var (service, transport, registry, cap) = MakeSystem();
        using var adapterCts = new CancellationTokenSource();

        var delay = new OneShotDelay();
        service.SetSweepDelayForTest(delay.InvokeAsync);
        // Clock only slightly ahead — well within the 1800s lease.
        service.SetClockForTest(() => DateTime.UtcNow + TimeSpan.FromSeconds(10));

        await service.StartAsync(transport.IncomingDatagrams, adapterCts.Token, CancellationToken.None);
        await transport.WriteAsync(SsdpDatagramBuilder.Notify("upnp:rootdevice", "ssdp:alive", RootBody));
        await WaitUntilAsync(() => registry.Count == 1);

        await delay.FirstAwaited;
        // Give the marshalled sweep a moment to run (it should evict nothing).
        await Task.Delay(50);

        await DrainSweepAsync(service, transport, adapterCts);

        registry.Count.Should().Be(1, "a device within its lease survives the sweep");
        cap.Entries.Should().NotContain(e => e.Category == DiagCategories.SsdpExpired);
    }

    [Fact]
    [Trait("fr", "FR-056")]
    public async Task Sweep_ByebyeMidWindow_StillRemovesImmediately_FR056()
    {
        // byebye wins immediately, independent of the lease (AC #3). The sweep is a no-op for that UDN.
        var (service, transport, registry, _) = MakeSystem();
        using var adapterCts = new CancellationTokenSource();

        var delay = new OneShotDelay();
        service.SetSweepDelayForTest(delay.InvokeAsync);
        service.SetClockForTest(() => DateTime.UtcNow + TimeSpan.FromSeconds(10)); // within lease

        await service.StartAsync(transport.IncomingDatagrams, adapterCts.Token, CancellationToken.None);
        await transport.WriteAsync(SsdpDatagramBuilder.Notify("upnp:rootdevice", "ssdp:alive", RootBody));
        await transport.WriteAsync(SsdpDatagramBuilder.Notify("upnp:rootdevice", "ssdp:byebye", RootBody));
        await WaitUntilAsync(() => registry.Count == 0);

        await delay.FirstAwaited;
        await DrainSweepAsync(service, transport, adapterCts);

        registry.Count.Should().Be(0, "byebye removed it immediately; the sweep is idempotent for that UDN");
    }

    [Fact]
    [Trait("fr", "FR-056")]
    public async Task Sweep_StopsOnAdapterTokenCancel_NoEvictionAfterTeardown_FR056()
    {
        var (service, transport, registry, cap) = MakeSystem();
        using var adapterCts = new CancellationTokenSource();

        // A delay that NEVER releases — the loop parks at its first await until the adapter token cancels.
        service.SetSweepDelayForTest((_, ct) => Task.Delay(Timeout.Infinite, ct));
        service.SetClockForTest(() => DateTime.UtcNow + TimeSpan.FromSeconds(2000)); // would expire if it ran

        await service.StartAsync(transport.IncomingDatagrams, adapterCts.Token, CancellationToken.None);
        await transport.WriteAsync(SsdpDatagramBuilder.Notify("upnp:rootdevice", "ssdp:alive", RootBody));
        await WaitUntilAsync(() => registry.Count == 1);

        // Cancel the adapter scope — the sweep loop's linked token cancels; the parked delay throws OCE.
        await adapterCts.CancelAsync();
        await DrainAsync(service, transport);

        registry.Count.Should().Be(1, "the sweep never ran a cycle (token cancelled) — no eviction after teardown");
        cap.Entries.Should().NotContain(e => e.Category == DiagCategories.SsdpExpired);
    }

    [Fact]
    [Trait("fr", "FR-056")]
    public async Task Sweep_RebindAsync_DrainsOldSweep_StartsFreshSweepOnNewScope_FR056()
    {
        // AC #7: an adapter switch (RebindAsync) must DRAIN the old sweep loop and start a FRESH one bound
        // to the new scope. The old sweep parks on a never-releasing delay (never evicts); RebindAsync must
        // still RETURN (proving StopSweep drained the parked loop — else `await _sweepLoop` would hang) AND
        // reset the single-start guard (else the inner StartAsync throws "already called"). The fresh sweep
        // (a OneShotDelay) then runs one cycle on the new scope and evicts the still-stale entry.
        var (service, transport1, registry, cap) = MakeSystem();
        using var adapterCts1 = new CancellationTokenSource();
        using var adapterCts2 = new CancellationTokenSource();
        string? removedUdn = null;
        registry.DeviceRemoved += id => removedUdn = id;

        // Scope 1: sweep parks forever (never evicts); clock far past the 1800s lease the builder advertises.
        service.SetSweepDelayForTest((_, ct) => Task.Delay(Timeout.Infinite, ct));
        service.SetClockForTest(() => DateTime.UtcNow + TimeSpan.FromSeconds(2000));

        await service.StartAsync(transport1.IncomingDatagrams, adapterCts1.Token, CancellationToken.None);
        await transport1.WriteAsync(SsdpDatagramBuilder.Notify("upnp:rootdevice", "ssdp:alive", RootBody));
        await WaitUntilAsync(() => registry.Count == 1);

        // Swap in a OneShotDelay for the post-rebind sweep, then rebind onto a fresh transport + adapter token.
        var sweep2 = new OneShotDelay();
        service.SetSweepDelayForTest(sweep2.InvokeAsync);
        var transport2 = new ChannelSsdpTransport();
        transport1.Complete(); // let the OLD read loop drain inside RebindAsync

        await service.RebindAsync(transport2.IncomingDatagrams, adapterCts2.Token, CancellationToken.None);

        await sweep2.FirstAwaited;
        await WaitUntilAsync(() => registry.Count == 0);

        await DrainSweepAsync(service, transport2, adapterCts2);

        registry.Count.Should().Be(0, "the FRESH post-rebind sweep evicted the (still-stale) entry");
        removedUdn.Should().Be(RootUdn, "the new-scope sweep uses the byebye-identical RemoveCore cascade");
        cap.Entries.Should().Contain(e =>
            e.Category == DiagCategories.SsdpExpired && e.Context.DeviceUuid == RootUdn,
            "the new-scope sweep emits the Ssdp.Expired diagnostic — it is genuinely running on the new scope");
    }

    [Fact]
    [Trait("fr", "FR-056")]
    public async Task Sweep_Eviction_IsMarshalledThroughUiDispatcher_ActionH_FR056()
    {
        // Action H (winui-no-synccontext-marshal-vm): the sweep mutates the registry off a TIMER thread.
        // Under a DeferredUiDispatcher the eviction MUST be queued (not applied inline) until Drain() —
        // proving it goes through IUiDispatcher.Post. (InlineUiDispatcher would mask this.)
        var cap = new CapturingDiagnosticEmitter();
        var ui = new DeferredUiDispatcher();
        var transport = new ChannelSsdpTransport();
        var registry = new DeviceRegistry(ui);
        var parser = new SsdpParser(cap);
        var service = new DiscoveryService(registry, parser, ui, cap);
        using var adapterCts = new CancellationTokenSource();

        var delay = new OneShotDelay();
        service.SetSweepDelayForTest(delay.InvokeAsync);
        // Stale lease: seed the entry "now", evaluate the sweep 2000s later (past the 1800s lease + jitter).
        var seedTime = DateTime.UtcNow;
        service.SetClockForTest(() => seedTime + TimeSpan.FromSeconds(2000));

        // Seed the entry deterministically. DeferredUiDispatcher.AssertOnUiThread is a no-op, so OnAlive
        // can be driven directly — no read-loop/sweep race over the seeding post.
        var location = new Uri("http://192.0.2.42:49152/desc.xml");
        registry.OnAlive(RootUdn, location, seedTime, "S", TimeSpan.FromSeconds(1800), null, null, CancellationToken.None);
        registry.Count.Should().Be(1, "entry seeded");

        var removed = new List<string>();
        registry.DeviceRemoved += removed.Add;

        await service.StartAsync(transport.IncomingDatagrams, adapterCts.Token, CancellationToken.None);

        // Let the single sweep cycle post its eviction (deferred — NOT applied yet).
        await delay.FirstAwaited;
        await WaitUntilAsync(() => ui.PostCount >= 1);

        registry.Count.Should().Be(1, "the sweep eviction is QUEUED, not applied — proves it went through Post");
        removed.Should().BeEmpty("no DeviceRemoved fires until the UI thread drains");

        ui.Drain(); // simulate the UI thread draining its queue

        registry.Count.Should().Be(0, "after Drain the marshalled eviction runs");
        removed.Should().ContainSingle().Which.Should().Be(RootUdn);
        cap.Entries.Should().ContainSingle(e =>
            e.Category == DiagCategories.SsdpExpired && e.Context.DeviceUuid == RootUdn);

        await adapterCts.CancelAsync(); // stop the sweep loop so DisposeAsync drains
        transport.Complete();
        await service.DisposeAsync();
    }

    // Poll a predicate to true within a short budget (the read loop + sweep run on background tasks;
    // the inline dispatcher runs their UI-thread work synchronously when posted).
    private static async Task WaitUntilAsync(Func<bool> predicate, int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!predicate() && DateTime.UtcNow < deadline)
            await Task.Delay(10);
    }
}
