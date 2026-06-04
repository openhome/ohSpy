namespace ohSpy.Core.Tests.ViewModels;

using System.Diagnostics;
using FluentAssertions;
using ohSpy.Core.Devices;
using ohSpy.Core.Events;
using ohSpy.Core.Http;
using ohSpy.Core.Models;
using ohSpy.Core.Tests.Fakes;
using ohSpy.Core.Threading;
using ohSpy.Core.ViewModels;

/// <summary>
/// Story 4.3 — <see cref="SubscriptionPopupViewModel"/> unit tests (the automated-test heart; the
/// App window/launcher are App-only → manual smoke). The headline coverage is the §0 marshalling
/// guard: every handle-event/replay mutation goes through <c>_ui.Post</c>, proved with
/// <see cref="DeferredUiDispatcher"/> (InlineUiDispatcher masks it — the 3.2 crash class).
/// </summary>
public sealed class SubscriptionPopupViewModelTests
{
    private static readonly Uri DeviceLocation = new("http://192.168.1.100:49152/desc.xml");
    private const string DeviceUdn = "uuid:33333333-3333-3333-3333-333333333333";

    private static ServiceDescription Service(
        string serviceType = "urn:schemas-upnp-org:service:AVTransport:1") =>
        new(serviceType, "urn:upnp-org:serviceId:AVTransport", "/AVT/Scpd.xml", "/AVT/ctrl", "/AVT/evt");

    private static RegistryEntry Entry(string? udn = null, CancellationToken token = default) =>
        new(udn ?? DeviceUdn, DeviceLocation, DateTime.UtcNow, token);

    private static EventNotification Notify(long seq, params (string Key, string Value)[] props) =>
        new("uuid:fake-sid-1", seq, DateTime.UtcNow,
            props.ToDictionary(p => p.Key, p => p.Value));

    private static SubscriptionPopupViewModel MakeVm(
        out FakeSubscriptionClient client,
        out FakeDeviceRegistry registry,
        IUiDispatcher? ui = null,
        ServiceDescription? service = null,
        RegistryEntry? entry = null)
    {
        client = new FakeSubscriptionClient();
        registry = new FakeDeviceRegistry();
        return new SubscriptionPopupViewModel(
            service ?? Service(), entry ?? Entry(), client,
            ui ?? new InlineUiDispatcher(), new CapturingDiagnosticEmitter(), registry);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition() && sw.ElapsedMilliseconds < timeoutMs)
            await Task.Delay(5);
        condition().Should().BeTrue($"the expected state was not reached within {timeoutMs}ms");
    }

    // ─── Shape / ctor (AC-4.3.1) ────────────────────────────────────────────────

    [Fact]
    [Trait("ac", "AC-4.3.1")]
    public void Ctor_TitleIsServiceTail_StatusSubscribing_EventsCap5000()
    {
        var vm = MakeVm(out _, out _);

        vm.Title.Should().Be("AVTransport:1");
        vm.Status.Should().Be(SubscriptionStatus.Subscribing);
        vm.Events.Capacity.Should().Be(5000);
        vm.Events.Count.Should().Be(0);
        vm.LatestPropertyValues.Should().BeEmpty();
    }

    // ─── Subscribe flow (AC-4.3.2) ──────────────────────────────────────────────

    [Fact]
    [Trait("ac", "AC-4.3.2")]
    public async Task InitializeAsync_Success_FlipsToSubscribed_WithSid()
    {
        var vm = MakeVm(out var client, out _);

        await vm.InitializeAsync();

        vm.Status.Should().Be(SubscriptionStatus.Subscribed);
        vm.StatusMessage.Should().Contain(client.LastHandle!.Sid);
        client.Calls.Should().ContainSingle();
    }

    [Fact]
    [Trait("ac", "AC-4.3.12")]
    public async Task InitializeAsync_SubscribedFlip_GoesThroughPost_DeferredGuard()
    {
        // The post-await Status=Subscribed apply MUST be marshalled (it runs off-thread). With a
        // DeferredUiDispatcher it stays Subscribing until Drain().
        var ui = new DeferredUiDispatcher();
        var vm = MakeVm(out _, out _, ui: ui);

        await vm.InitializeAsync();

        vm.Status.Should().Be(SubscriptionStatus.Subscribing, "the apply is queued, not run inline");
        ui.PostCount.Should().BeGreaterThan(0);

        ui.Drain();
        vm.Status.Should().Be(SubscriptionStatus.Subscribed);
    }

    // ─── NOTIFY → newest-first list + latest-values (AC-4.3.3) ───────────────────

    [Fact]
    [Trait("ac", "AC-4.3.3")]
    public async Task Notification_PrependsNewestFirst_AndMergesLatest()
    {
        var vm = MakeVm(out var client, out _);
        await vm.InitializeAsync();
        var handle = client.LastHandle!;

        handle.RaiseNotification(Notify(0, ("TransportState", "PLAYING")));
        handle.RaiseNotification(Notify(1, ("TransportState", "PAUSED_PLAYBACK"), ("Volume", "42")));

        // newest-first: the seq-1 event is at index 0
        vm.Events.Count.Should().Be(2);
        vm.Events[0].Seq.Should().Be(1);
        vm.Events[1].Seq.Should().Be(0);

        // latest-values: last-write-wins, append-on-first-seen (stable order)
        vm.LatestPropertyValues.Select(r => r.Name).Should().Equal("TransportState", "Volume");
        vm.LatestPropertyValues.Single(r => r.Name == "TransportState").Value.Should().Be("PAUSED_PLAYBACK");
        vm.LatestPropertyValues.Single(r => r.Name == "Volume").Value.Should().Be("42");
    }

    [Fact]
    [Trait("ac", "AC-4.3.12")]
    public async Task Notification_IsMarshalled_DeferredGuard()
    {
        // §0 / retro Action H: a delivered NOTIFY must NOT touch the bound list/latest-values until
        // the dispatcher drains. (InlineUiDispatcher would mask this — the exact 3.2 crash class.)
        var ui = new DeferredUiDispatcher();
        var vm = MakeVm(out var client, out _, ui: ui);
        await vm.InitializeAsync();
        ui.Drain(); // attach the handlers + flip Subscribed
        var handle = client.LastHandle!;

        handle.RaiseNotification(Notify(7, ("X", "1")));

        vm.Events.Count.Should().Be(0, "the append is queued, not applied inline");
        vm.LatestPropertyValues.Should().BeEmpty();

        ui.Drain();
        vm.Events.Count.Should().Be(1);
        vm.LatestPropertyValues.Should().ContainSingle().Which.Value.Should().Be("1");
    }

    [Fact]
    [Trait("ac", "AC-4.3.2")]
    public async Task ReplayBuffer_PreAttachEvent_IsDeliveredAndMarshalled()
    {
        // AC-4.2.7 replay: a NOTIFY that arrived BEFORE the VM attached is flushed inside `add` (on the
        // off-thread continuation) → the VM's OnNotification re-marshals it. Drive: build a handle, raise
        // a notification BEFORE the VM attaches, then run InitializeAsync against a client that returns it.
        var ui = new DeferredUiDispatcher();
        var registry = new FakeDeviceRegistry();
        var preBuilt = FakeSubscriptionClient.NewHandle("uuid:fake-sid-1");
        preBuilt.RaiseNotification(Notify(0, ("PreAttach", "yes"))); // buffered (no subscriber yet)
        var client = new HandReturningClient(preBuilt);
        var vm = new SubscriptionPopupViewModel(
            Service(), Entry(), client, ui, new CapturingDiagnosticEmitter(), registry);

        await vm.InitializeAsync();
        ui.Drain(); // runs the attach (flushes the replay → OnNotification posts) + Subscribed flip
        ui.Drain(); // runs the queued OnNotification apply

        vm.Events.Should().ContainSingle().Which.Properties["PreAttach"].Should().Be("yes");
        vm.LatestPropertyValues.Should().ContainSingle().Which.Value.Should().Be("yes");
    }

    [Fact]
    [Trait("ac", "AC-4.3.4")]
    public async Task ReplayBuffer_PreAttachLapse_IsDeliveredAndMarshalled()
    {
        // AC-4.2.7 replay (the lapse sibling of the notification drill): a Lapsed that fired BEFORE the
        // VM attached is flushed inside `add` on the off-thread continuation → OnLapsed must re-marshal
        // it (not mutate Status inline). Two Drain()s: attach+flush, then the queued lapse apply.
        var ui = new DeferredUiDispatcher();
        var registry = new FakeDeviceRegistry();
        var preBuilt = FakeSubscriptionClient.NewHandle("uuid:fake-sid-1");
        preBuilt.RaiseLapsed(SubscriptionLapseReason.RenewRefused); // buffered (no subscriber yet)
        var client = new HandReturningClient(preBuilt);
        var vm = new SubscriptionPopupViewModel(
            Service(), Entry(), client, ui, new CapturingDiagnosticEmitter(), registry);

        await vm.InitializeAsync();
        ui.Drain(); // attach (flushes the replay lapse → OnLapsed posts) + Subscribed flip
        ui.Drain(); // runs the queued OnLapsed apply

        vm.Status.Should().Be(SubscriptionStatus.Lapsed);
        vm.StatusMessage.Should().Be("subscription lapsed (renewal refused / failed)");
    }

    // ─── Lapse → banner (AC-4.3.4) ──────────────────────────────────────────────

    [Theory]
    [Trait("ac", "AC-4.3.4")]
    [InlineData(SubscriptionLapseReason.DeviceGone, SubscriptionStatus.DeviceGone, "device no longer reachable")]
    [InlineData(SubscriptionLapseReason.AdapterSwitch, SubscriptionStatus.Lapsed, "device unreachable after adapter switch")]
    [InlineData(SubscriptionLapseReason.RenewRefused, SubscriptionStatus.Lapsed, "subscription lapsed (renewal refused / failed)")]
    [InlineData(SubscriptionLapseReason.RenewTransportError, SubscriptionStatus.Lapsed, "subscription lapsed (renewal refused / failed)")]
    public async Task Lapse_SetsReasonSpecificBanner(
        SubscriptionLapseReason reason, SubscriptionStatus expectedStatus, string expectedText)
    {
        var vm = MakeVm(out var client, out _);
        await vm.InitializeAsync();

        client.LastHandle!.RaiseLapsed(reason);

        vm.Status.Should().Be(expectedStatus);
        vm.StatusMessage.Should().Be(expectedText);
    }

    [Fact]
    [Trait("ac", "AC-4.3.4")]
    public async Task Lapse_KeepsAlreadyShownEvents()
    {
        var vm = MakeVm(out var client, out _);
        await vm.InitializeAsync();
        var handle = client.LastHandle!;
        handle.RaiseNotification(Notify(0, ("A", "1")));

        handle.RaiseLapsed(SubscriptionLapseReason.RenewRefused);

        vm.Events.Should().ContainSingle("already-shown events remain after a lapse");
        vm.Status.Should().Be(SubscriptionStatus.Lapsed);
    }

    [Fact]
    [Trait("ac", "AC-4.3.12")]
    public async Task Lapse_IsMarshalled_DeferredGuard()
    {
        var ui = new DeferredUiDispatcher();
        var vm = MakeVm(out var client, out _, ui: ui);
        await vm.InitializeAsync();
        ui.Drain();

        client.LastHandle!.RaiseLapsed(SubscriptionLapseReason.DeviceGone);

        vm.Status.Should().Be(SubscriptionStatus.Subscribed, "the lapse apply is queued, not inline");
        ui.Drain();
        vm.Status.Should().Be(SubscriptionStatus.DeviceGone);
    }

    // ─── DeviceRemoved banner (AC-4.3.6 / FR-037) ───────────────────────────────

    [Fact]
    [Trait("ac", "AC-4.3.9")]
    public async Task DeviceRemoved_MatchingUuid_FlipsToDeviceGone()
    {
        var vm = MakeVm(out _, out var registry);
        await vm.InitializeAsync();

        registry.RaiseDeviceRemoved(DeviceUdn);

        vm.Status.Should().Be(SubscriptionStatus.DeviceGone);
    }

    [Fact]
    [Trait("ac", "AC-4.3.9")]
    public async Task DeviceRemoved_OtherUuid_Ignored()
    {
        var vm = MakeVm(out _, out var registry);
        await vm.InitializeAsync();

        registry.RaiseDeviceRemoved($"uuid:{Guid.NewGuid()}");

        vm.Status.Should().Be(SubscriptionStatus.Subscribed);
    }

    // Amendment A30 regression (f): a DIFFERENT-CASED string UDN still flips the banner (OrdinalIgnoreCase).
    [Fact]
    [Trait("ac", "AC-2.4.1")]
    public async Task DeviceRemoved_DifferentCasedUdn_FlipsToDeviceGone_OrdinalIgnoreCase()
    {
        var vm = MakeVm(out _, out var registry);
        await vm.InitializeAsync();

        registry.RaiseDeviceRemoved(DeviceUdn.ToUpperInvariant()); // same device, different case

        vm.Status.Should().Be(SubscriptionStatus.DeviceGone,
            "OrdinalIgnoreCase UDN match flips the FR-037 banner (Amendment A30)");
    }

    [Fact]
    [Trait("ac", "AC-4.3.4")]
    public async Task DeviceGone_DualPath_IsIdempotent()
    {
        // Both DeviceRemoved (registry) AND Lapsed(DeviceGone) (handle) converge on DeviceGone.
        var vm = MakeVm(out var client, out var registry);
        await vm.InitializeAsync();

        registry.RaiseDeviceRemoved(DeviceUdn);
        client.LastHandle!.RaiseLapsed(SubscriptionLapseReason.DeviceGone);

        vm.Status.Should().Be(SubscriptionStatus.DeviceGone);
    }

    // ─── Failed subscribe (AC-4.3.5) ────────────────────────────────────────────

    [Fact]
    [Trait("ac", "AC-4.3.5")]
    public async Task FailedSubscribe_TypedUpnpException_FlipsToFailed_NoUnsubscribe()
    {
        var vm = MakeVm(out var client, out _);
        client.ThrowOnSubscribe = new UpnpTransportException(
            new Uri("http://x/evt"), "503 Service Unavailable", 503);

        await vm.InitializeAsync();

        vm.Status.Should().Be(SubscriptionStatus.FailedToSubscribe);
        vm.StatusMessage.Should().Contain("503");
        vm.Dispose(); // close performs NO UNSUBSCRIBE (no handle)
        client.CloseCount.Should().Be(0);
    }

    [Fact]
    [Trait("ac", "AC-4.3.5")]
    public async Task FailedSubscribe_UnexpectedException_FlipsToFailed_Defensive()
    {
        var vm = MakeVm(out var client, out _);
        client.ThrowOnSubscribe = new InvalidOperationException("boom");

        await vm.InitializeAsync();

        vm.Status.Should().Be(SubscriptionStatus.FailedToSubscribe);
        vm.StatusMessage.Should().Contain("boom");
    }

    [Fact]
    [Trait("ac", "AC-4.3.5")]
    public async Task Subscribe_Cancelled_Swallowed_NoStatusFlip()
    {
        using var cts = new CancellationTokenSource();
        var gate = new TaskCompletionSource();
        var vm = MakeVm(out var client, out _, entry: Entry(token: cts.Token));
        client.SubscribeGate = gate.Task; // hold subscribe open

        var task = vm.InitializeAsync();
        await cts.CancelAsync();           // popup-close cancellation during subscribe
        gate.SetResult();
        await task;

        vm.Status.Should().Be(SubscriptionStatus.Subscribing, "OCE during subscribe is swallowed");
    }

    // ─── FIFO eviction (AC-4.3.6) ───────────────────────────────────────────────

    [Fact]
    [Trait("ac", "AC-4.3.6")]
    public async Task Notifications_AtCapacity_EvictOldest_CountStays5000()
    {
        var vm = MakeVm(out var client, out _);
        await vm.InitializeAsync();
        var handle = client.LastHandle!;

        for (long i = 0; i < 5001; i++)
            handle.RaiseNotification(Notify(i, ("Seq", i.ToString(System.Globalization.CultureInfo.InvariantCulture))));

        vm.Events.Count.Should().Be(5000, "the 5,001st event evicts the oldest tail");
        vm.Events[0].Seq.Should().Be(5000, "newest is at index 0");
        vm.Events[4999].Seq.Should().Be(1, "the seq-0 event was evicted");
    }

    // ─── Dispose / close cascade (AC-4.3.9) ─────────────────────────────────────

    [Fact]
    [Trait("ac", "AC-4.3.9")]
    public async Task Dispose_CallsCloseAsync_DetachesHandlers()
    {
        var vm = MakeVm(out var client, out _);
        await vm.InitializeAsync();

        vm.Dispose();

        client.CloseCount.Should().Be(1, "Dispose fire-and-forgets handle.CloseAsync");
        // post-dispose events do not mutate state (handlers detached)
        client.LastHandle!.RaiseNotification(Notify(0, ("Z", "z")));
        vm.Events.Should().BeEmpty();
    }

    [Fact]
    [Trait("ac", "AC-4.3.9")]
    public async Task Dispose_IsIdempotent()
    {
        var vm = MakeVm(out var client, out _);
        await vm.InitializeAsync();

        vm.Dispose();
        vm.Dispose();

        client.CloseCount.Should().Be(1, "CloseAsync is invoked once (Interlocked once-guard)");
    }

    [Fact]
    [Trait("ac", "AC-4.3.9")]
    public async Task DisposedDuringAwait_ClosesFreshHandle_NoAttach_NoLeak()
    {
        // The popup is disposed (closed) AFTER SubscribeAsync returns a handle but BEFORE the marshalled
        // attach/Subscribed apply runs. The success-Post guard must close the freshly-returned handle
        // (no leaked subscription) and NOT attach handlers. Regression for the disposed-during-await guard.
        var ui = new DeferredUiDispatcher();
        var vm = MakeVm(out var client, out _, ui: ui);

        await vm.InitializeAsync(); // SubscribeAsync resolved; the success apply is QUEUED, not run
        vm.Dispose();               // popup closed before the attach drains
        ui.Drain();                 // the guarded success lambda runs and closes the fresh handle

        client.CloseCount.Should().Be(1, "the freshly-returned handle is closed when the popup disposes mid-await");
        vm.Status.Should().Be(SubscriptionStatus.Subscribing, "no Subscribed flip after dispose-during-await");

        // handlers were never attached → a late NOTIFY does not mutate the torn-down VM
        client.LastHandle!.RaiseNotification(Notify(0, ("Z", "z")));
        ui.Drain();
        vm.Events.Should().BeEmpty();
    }

    // ─── Multiple concurrent independent popups + non-serial drill (AC-4.3.7 / .13) ──

    [Fact]
    [Trait("ac", "AC-4.3.7")]
    public async Task FiveConcurrentPopups_RenderIndependently()
    {
        var vms = new List<SubscriptionPopupViewModel>();
        var clients = new List<FakeSubscriptionClient>();
        for (int i = 0; i < 5; i++)
        {
            var vm = MakeVm(out var client, out _, service: Service($"urn:schemas-upnp-org:service:Svc{i}:1"));
            await vm.InitializeAsync();
            vms.Add(vm);
            clients.Add(client);
        }

        // Feed each VM a different count of events; closing one must not affect the others.
        for (int i = 0; i < 5; i++)
            for (int e = 0; e <= i; e++)
                clients[i].LastHandle!.RaiseNotification(Notify(e, ("Idx", i.ToString(System.Globalization.CultureInfo.InvariantCulture))));

        vms[2].Dispose(); // close one mid-stream

        vms[0].Events.Count.Should().Be(1);
        vms[1].Events.Count.Should().Be(2);
        vms[3].Events.Count.Should().Be(4);
        vms[4].Events.Count.Should().Be(5);
        vms[0].Status.Should().Be(SubscriptionStatus.Subscribed);
        vms[3].Status.Should().Be(SubscriptionStatus.Subscribed);
        clients[2].CloseCount.Should().Be(1);
        clients[0].CloseCount.Should().Be(0, "closing VM 2 did not close VM 0");
    }

    [Fact]
    [Trait("ac", "AC-4.3.13")]
    public async Task SlowNotificationOnVmA_DoesNotBlockVmB()
    {
        // FR-104 at the VM layer: a delayed (queued, undrained) notification on VM A must not hold up
        // VM B's delivery — proven by giving each VM its own deferred dispatcher; draining B alone
        // delivers B's event while A's stays pending.
        var uiA = new DeferredUiDispatcher();
        var uiB = new DeferredUiDispatcher();
        var vmA = MakeVm(out var clientA, out _, ui: uiA, service: Service("urn:schemas-upnp-org:service:A:1"));
        var vmB = MakeVm(out var clientB, out _, ui: uiB, service: Service("urn:schemas-upnp-org:service:B:1"));
        await vmA.InitializeAsync();
        await vmB.InitializeAsync();
        uiA.Drain();
        uiB.Drain();

        clientA.LastHandle!.RaiseNotification(Notify(0, ("A", "1"))); // queued on A, not drained
        clientB.LastHandle!.RaiseNotification(Notify(0, ("B", "1")));

        uiB.Drain(); // drain B only

        vmB.Events.Should().ContainSingle("B's notification is delivered independently of A");
        vmA.Events.Should().BeEmpty("A's notification is still pending — it did not block B");
    }

    // A minimal ISubscriptionClient that returns a caller-supplied handle (for the replay-buffer drill).
    private sealed class HandReturningClient : ISubscriptionClient
    {
        private readonly SubscriptionHandle _handle;
        public HandReturningClient(SubscriptionHandle handle) => _handle = handle;
        public void SetAdapterContext(CancellationToken adapterToken) { }
        public void SetCallbackHost(IEventCallbackHost callbackHost) { }
        public Task<SubscriptionHandle> SubscribeAsync(
            ServiceDescription service, RegistryEntry parentEntry, CancellationToken popupToken) =>
            Task.FromResult(_handle);
    }
}
