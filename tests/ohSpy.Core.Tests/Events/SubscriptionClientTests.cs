namespace ohSpy.Core.Tests.Events;

using System.Text;
using FluentAssertions;
using ohSpy.Core.Devices;
using ohSpy.Core.Diagnostics;
using ohSpy.Core.Events;
using ohSpy.Core.Http;
using ohSpy.Core.Models;
using ohSpy.Core.Tests.Fakes;
using Xunit;

/// <summary>
/// Story 4.2 — the GENA subscription lifecycle orchestrator. Drives every AC against a controllable
/// <see cref="StubUpnpHttpClient"/> (SUBSCRIBE/RENEW/UNSUBSCRIBE responders + recorded calls) and a
/// <see cref="FakeEventCallbackHost"/> (settable CallbackBaseUrl + raise NotifyReceived). No real
/// device — the lifecycle contract IS the test contract. Auto-renew timing uses the internal
/// delay-func seam so tests run in ms, not minutes.
/// </summary>
public sealed class SubscriptionClientTests
{
    private static readonly Uri Location = new("http://192.168.1.50:1400/desc.xml");
    private const string EventSubRel = "evt/svc";
    private static readonly Uri ExpectedEventSubUrl = new(Location, EventSubRel);

    private static ServiceDescription Service(string eventSub = EventSubRel) =>
        new("urn:schemas-upnp-org:service:Foo:1", "urn:upnp-org:serviceId:Foo", "scpd.xml", "ctrl", eventSub);

    private static RegistryEntry Entry(CancellationToken deviceLevelToken = default) =>
        new($"uuid:{Guid.NewGuid()}", Location, DateTime.UtcNow, deviceLevelToken);

    private static byte[] Propertyset(params (string Name, string Value)[] props)
    {
        var sb = new StringBuilder();
        sb.Append("<e:propertyset xmlns:e=\"urn:schemas-upnp-org:event-1-0\">");
        foreach (var (n, v) in props)
        {
            sb.Append("<e:property><").Append(n).Append('>').Append(v).Append("</").Append(n).Append("></e:property>");
        }
        sb.Append("</e:propertyset>");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static NotifyRequest Notify(string sid, byte[] body, long seq = 0) =>
        new(sid, seq, "/", body, DateTime.UtcNow);

    private sealed record Harness(
        SubscriptionClient Client,
        StubUpnpHttpClient Http,
        FakeEventCallbackHost Host,
        CapturingDiagnosticEmitter Diag);

    private static Harness NewHarness(
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        CancellationToken adapterToken = default)
    {
        var http = new StubUpnpHttpClient();
        var host = new FakeEventCallbackHost();
        var diag = new CapturingDiagnosticEmitter();
        var client = delay is null
            ? new SubscriptionClient(http, diag)
            : new SubscriptionClient(http, diag, delay);
        client.SetCallbackHost(host); // A23: host bound post-construction (ShellViewModel precedent)
        client.SetAdapterContext(adapterToken);
        return new Harness(client, http, host, diag);
    }

    // A delay seam that NEVER fires (so the renew loop parks indefinitely) — for tests that don't
    // exercise renewal. Honours cancellation so teardown is clean.
    private static Task NeverDelayAsync(TimeSpan _, CancellationToken ct) => Task.Delay(Timeout.Infinite, ct);

    // ── AC-4.2.5 / AC-4.2.6 happy path ──────────────────────────────────────────
    [Fact]
    [Trait("ac", "AC-4.2.5")]
    public async Task HappySubscribe_RegistersSid_ResolvesAbsoluteUrl_ReturnsHandle()
    {
        var h = NewHarness(NeverDelayAsync);
        h.Http.SubscribeResponder = (_, _, _, _) => Task.FromResult(new SubscribeResponse("uuid:sid-1", TimeSpan.FromSeconds(300)));

        var handle = await h.Client.SubscribeAsync(Service(), Entry(), CancellationToken.None);

        handle.Sid.Should().Be("uuid:sid-1");
        var call = h.Http.GenaCalls.Single(c => c.Verb == "SUBSCRIBE");
        call.EventSubUrl.Should().Be(ExpectedEventSubUrl); // relative resolved against LocationUrl
        call.CallbackUrl.Should().Be(h.Host.CallbackBaseUrl);
        call.RequestedTimeout.Should().Be(TimeSpan.FromSeconds(300));
        h.Diag.Entries.Should().Contain(e => e.Category == DiagCategories.GenaSubscribe);
    }

    [Fact]
    [Trait("ac", "AC-4.2.6")]
    public async Task Notify_RoutesToHandle_BySid()
    {
        var h = NewHarness(NeverDelayAsync);
        h.Http.SubscribeResponder = (_, _, _, _) => Task.FromResult(new SubscribeResponse("sid-A", TimeSpan.FromSeconds(300)));
        var handle = await h.Client.SubscribeAsync(Service(), Entry(), CancellationToken.None);

        var received = new List<EventNotification>();
        handle.NotificationReceived += received.Add;

        await h.Host.RaiseNotifyAsync(Notify("sid-A", Propertyset(("Volume", "42"))));

        await WaitUntilAsync(() => received.Count == 1);
        received.Single().Properties["Volume"].Should().Be("42");
        received.Single().Sid.Should().Be("sid-A");
    }

    [Fact]
    [Trait("ac", "AC-4.2.6")]
    public async Task Notify_UnknownSid_NoLiveSubscribe_DroppedSilently()
    {
        var h = NewHarness(NeverDelayAsync);
        // No subscription created → no SUBSCRIBE in flight → unknown SID must be a silent drop (no throw).
        Func<Task> act = () => h.Host.RaiseNotifyAsync(Notify("ghost", Propertyset(("X", "1"))));
        await act.Should().NotThrowAsync();
    }

    // ── AC-4.2.7 NOTIFY-before-SID race ──────────────────────────────────────────
    [Fact]
    [Trait("ac", "AC-4.2.7")]
    public async Task NotifyBeforeSid_Race_IsBufferedAndReplayed()
    {
        var h = NewHarness(NeverDelayAsync);
        var gate = new TaskCompletionSource();
        var subscribeInFlight = new TaskCompletionSource();
        var sidKnown = new TaskCompletionSource();

        // The SUBSCRIBE responder signals it has been ENTERED (so the pending buffer is registered),
        // then blocks until the test has fired the racing NOTIFY — guaranteeing the NOTIFY lands BEFORE
        // the SID is registered.
        h.Http.SubscribeResponder = async (_, _, _, _) =>
        {
            subscribeInFlight.TrySetResult();
            await gate.Task;
            return new SubscribeResponse("sid-race", TimeSpan.FromSeconds(300));
        };

        SubscriptionHandle? handle = null;
        var received = new List<EventNotification>();
        var subscribeTask = Task.Run(async () =>
        {
            handle = await h.Client.SubscribeAsync(Service(), Entry(), CancellationToken.None);
            handle.NotificationReceived += received.Add; // attach AFTER subscribe; replay must still reach it
            sidKnown.SetResult();
        });

        // Wait until SUBSCRIBE is in flight (pending buffer registered, SID not yet known), THEN fire
        // the racing NOTIFY.
        await subscribeInFlight.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await h.Host.RaiseNotifyAsync(Notify("sid-race", Propertyset(("Power", "ON"))));

        // Now let SUBSCRIBE complete; the buffered NOTIFY must be replayed to the handle.
        gate.SetResult();
        await subscribeTask;
        await sidKnown.Task;

        await WaitUntilAsync(() => received.Count == 1);
        received.Single().Properties["Power"].Should().Be("ON");
    }

    // ── AC-4.2.8 propertyset parse + malformed swallow ───────────────────────────
    [Fact]
    [Trait("ac", "AC-4.2.8")]
    public async Task Propertyset_MultipleProperties_AllParsed()
    {
        var h = NewHarness(NeverDelayAsync);
        h.Http.SubscribeResponder = (_, _, _, _) => Task.FromResult(new SubscribeResponse("sid-P", TimeSpan.FromSeconds(300)));
        var handle = await h.Client.SubscribeAsync(Service(), Entry(), CancellationToken.None);
        var received = new List<EventNotification>();
        handle.NotificationReceived += received.Add;

        await h.Host.RaiseNotifyAsync(Notify("sid-P", Propertyset(("Volume", "10"), ("Mute", "0")), seq: 7));

        await WaitUntilAsync(() => received.Count == 1);
        var ev = received.Single();
        ev.Properties.Should().HaveCount(2);
        ev.Properties["Volume"].Should().Be("10");
        ev.Properties["Mute"].Should().Be("0");
        ev.Seq.Should().Be(7);
    }

    [Fact]
    [Trait("ac", "AC-4.2.8")]
    public async Task Propertyset_Malformed_Swallowed_NoLapse_NoCrash()
    {
        var h = NewHarness(NeverDelayAsync);
        h.Http.SubscribeResponder = (_, _, _, _) => Task.FromResult(new SubscribeResponse("sid-M", TimeSpan.FromSeconds(300)));
        var handle = await h.Client.SubscribeAsync(Service(), Entry(), CancellationToken.None);
        var received = new List<EventNotification>();
        var lapses = new List<SubscriptionLapseReason>();
        handle.NotificationReceived += received.Add;
        handle.Lapsed += lapses.Add;

        // Malformed body → the host's awaited handler must not throw.
        Func<Task> act = () => h.Host.RaiseNotifyAsync(Notify("sid-M", Encoding.UTF8.GetBytes("<not-xml")));
        await act.Should().NotThrowAsync();

        // Then a VALID notify must still arrive — proving the subscription did not lapse.
        await h.Host.RaiseNotifyAsync(Notify("sid-M", Propertyset(("OK", "1"))));
        await WaitUntilAsync(() => received.Count == 1);
        received.Single().Properties["OK"].Should().Be("1");
        lapses.Should().BeEmpty();
    }

    // ── AC-4.2.9 non-serial across subscriptions ─────────────────────────────────
    [Fact]
    [Trait("ac", "AC-4.2.9")]
    public async Task SlowParseOnA_DoesNotBlockB()
    {
        var h = NewHarness(NeverDelayAsync);
        h.Http.SubscribeResponder = (url, _, _, _) =>
            Task.FromResult(new SubscribeResponse(url.AbsoluteUri.Contains("aaa") ? "sid-A" : "sid-B", TimeSpan.FromSeconds(300)));

        var entry = Entry();
        var handleA = await h.Client.SubscribeAsync(Service("aaa"), entry, CancellationToken.None);
        var handleB = await h.Client.SubscribeAsync(Service("bbb"), entry, CancellationToken.None);

        var aDelay = TimeSpan.FromMilliseconds(200);
        var aStarted = new TaskCompletionSource();
        var bObservedAt = new TaskCompletionSource<TimeSpan>();
        var sw = new System.Diagnostics.Stopwatch();

        handleA.NotificationReceived += _ =>
        {
            aStarted.TrySetResult();
            Thread.Sleep(aDelay); // simulate a slow parse/handler on A
        };
        handleB.NotificationReceived += _ => bObservedAt.TrySetResult(sw.Elapsed);

        // Fire A first (it will block its own worker), then B — B is on a SEPARATE worker.
        await h.Host.RaiseNotifyAsync(Notify("sid-A", Propertyset(("A", "1"))));
        await aStarted.Task; // ensure A's slow handler is RUNNING (mid 200 ms sleep) before timing B
        sw.Start();         // measure B's latency from the moment B is dispatched, not from test start
        await h.Host.RaiseNotifyAsync(Notify("sid-B", Propertyset(("B", "1"))));

        var bAt = await bObservedAt.Task.WaitAsync(TimeSpan.FromSeconds(2));
        // B must be observed well before A's 200 ms sleep would have finished — i.e. it ran on a
        // SEPARATE worker, not serialized behind A. A generous bound (still << 200 ms) keeps this
        // robust under CI CPU contention while still proving non-serial dispatch.
        bAt.Should().BeLessThan(TimeSpan.FromMilliseconds(180), "B must not wait on A's 200 ms parse");
    }

    // ── AC-4.2.10 failed SUBSCRIBE → no SID, no UNSUBSCRIBE ───────────────────────
    [Fact]
    [Trait("ac", "AC-4.2.10")]
    public async Task FailedSubscribe_Throws_NoSid_NoUnsubscribe_EmitsFailed()
    {
        var h = NewHarness(NeverDelayAsync);
        h.Http.SubscribeResponder = (url, _, _, _) =>
            Task.FromException<SubscribeResponse>(new UpnpTransportException(url, "500", 500));

        Func<Task> act = () => h.Client.SubscribeAsync(Service(), Entry(), CancellationToken.None);

        await act.Should().ThrowAsync<UpnpTransportException>();
        h.Http.CountOf("UNSUBSCRIBE").Should().Be(0);
        h.Diag.Entries.Should().Contain(e => e.Category == DiagCategories.GenaSubscribeFailed && e.Severity == "Warning");
    }

    [Fact]
    [Trait("ac", "AC-4.2.10")]
    public async Task MalformedEventSubUrl_FailsLikeTransport_NoSid()
    {
        var h = NewHarness(NeverDelayAsync);
        // A string Uri.TryCreate genuinely rejects against the base (malformed IPv6 authority) — the
        // guard must short-circuit to a transport-like failure with NO SID and NO SUBSCRIBE call.
        var svc = Service("http://[::bad");
        Func<Task> act = () => h.Client.SubscribeAsync(svc, Entry(), CancellationToken.None);

        await act.Should().ThrowAsync<UpnpException>();
        h.Http.CountOf("SUBSCRIBE").Should().Be(0);
        h.Http.CountOf("UNSUBSCRIBE").Should().Be(0);
        h.Diag.Entries.Should().Contain(e => e.Category == DiagCategories.GenaSubscribeFailed);
    }

    // ── AC-4.2.11 / AC-4.2.16 auto-renew via the delay seam ──────────────────────
    [Fact]
    [Trait("ac", "AC-4.2.11")]
    public async Task AutoRenew_FiresBeforeExpiry_AndReschedules_ViaDelaySeam()
    {
        // The delay seam fires immediately (we control timing, no real wait). Cap renews so the test
        // doesn't spin forever.
        var renewGate = new SemaphoreSlim(0);
        var delayCalls = 0;
        Func<TimeSpan, CancellationToken, Task> delay = (d, ct) =>
        {
            Interlocked.Increment(ref delayCalls);
            return Task.CompletedTask; // fast-forward
        };

        var h = NewHarness(delay);
        h.Http.SubscribeResponder = (_, _, _, _) => Task.FromResult(new SubscribeResponse("sid-R", TimeSpan.FromSeconds(300)));

        var renewCount = 0;
        h.Http.RenewResponder = (_, sid, _, _) =>
        {
            var n = Interlocked.Increment(ref renewCount);
            if (n >= 3)
            {
                renewGate.Release();
                // Park further renews by lapsing cleanly via a 412 after we've proven reschedule.
                return Task.FromException<SubscribeResponse>(new UpnpTransportException(ExpectedEventSubUrl, "412", 412));
            }
            return Task.FromResult(new SubscribeResponse(sid, TimeSpan.FromSeconds(300)));
        };

        var handle = await h.Client.SubscribeAsync(Service(), Entry(), CancellationToken.None);

        (await renewGate.WaitAsync(TimeSpan.FromSeconds(30))).Should().BeTrue("renewal should fire and reschedule");
        renewCount.Should().BeGreaterThanOrEqualTo(3, "the loop rescheduled across multiple granted leases");
        h.Http.GenaCalls.Count(c => c.Verb == "RENEW").Should().BeGreaterThanOrEqualTo(3);
    }

    [Fact]
    [Trait("ac", "AC-4.2.16")]
    public async Task RenewDelay_UsesEightyPercentOfGrantedLease()
    {
        TimeSpan? observedDelay = null;
        var delayObserved = new TaskCompletionSource();
        Func<TimeSpan, CancellationToken, Task> delay = (d, ct) =>
        {
            if (observedDelay is null)
            {
                observedDelay = d;
                delayObserved.TrySetResult();
            }
            return Task.Delay(Timeout.Infinite, ct); // park after first observation
        };

        var h = NewHarness(delay);
        h.Http.SubscribeResponder = (_, _, _, _) => Task.FromResult(new SubscribeResponse("sid-D", TimeSpan.FromSeconds(300)));
        h.Http.RenewResponder = (_, sid, _, _) => Task.FromResult(new SubscribeResponse(sid, TimeSpan.FromSeconds(300)));

        await h.Client.SubscribeAsync(Service(), Entry(), CancellationToken.None);

        await delayObserved.Task.WaitAsync(TimeSpan.FromSeconds(30));
        // 80% of 300 s = 240 s (and 300-30 = 270, so 240 is the min → 240).
        observedDelay!.Value.Should().Be(TimeSpan.FromSeconds(240));
    }

    // ── AC-4.2.12 renew failure → lapse, no retry, no unsubscribe ────────────────
    [Fact]
    [Trait("ac", "AC-4.2.12")]
    public async Task Renew412_Lapses_RenewRefused_NoRetry_NoUnsubscribe()
    {
        // The delay fires immediately; the lapse may race ahead of the handler attach, but the handle
        // REPLAYS a pre-attach lapse to the first subscriber, so the assertion is deterministic.
        var h = NewHarness((_, _) => Task.CompletedTask);
        h.Http.SubscribeResponder = (_, _, _, _) => Task.FromResult(new SubscribeResponse("sid-412", TimeSpan.FromSeconds(300)));
        var renewCount = 0;
        h.Http.RenewResponder = (url, _, _, _) =>
        {
            Interlocked.Increment(ref renewCount);
            return Task.FromException<SubscribeResponse>(new UpnpTransportException(url, "412", 412));
        };

        var handle = await h.Client.SubscribeAsync(Service(), Entry(), CancellationToken.None);
        var lapses = new List<SubscriptionLapseReason>();
        var lapsed = new TaskCompletionSource();
        handle.Lapsed += r => { lapses.Add(r); lapsed.TrySetResult(); };

        await lapsed.Task.WaitAsync(TimeSpan.FromSeconds(30));
        lapses.Single().Should().Be(SubscriptionLapseReason.RenewRefused);
        renewCount.Should().Be(1, "no retry after a refused renew");
        h.Diag.Entries.Should().Contain(e => e.Category == DiagCategories.GenaRenewFailed);

        // A lapsed subscription's CloseAsync sends NO UNSUBSCRIBE.
        await handle.CloseAsync();
        h.Http.CountOf("UNSUBSCRIBE").Should().Be(0);
    }

    [Fact]
    [Trait("ac", "AC-4.2.12")]
    public async Task RenewTransportFail_Lapses_RenewTransportError()
    {
        var h = NewHarness((_, _) => Task.CompletedTask);
        h.Http.SubscribeResponder = (_, _, _, _) => Task.FromResult(new SubscribeResponse("sid-T", TimeSpan.FromSeconds(300)));
        h.Http.RenewResponder = (url, _, _, _) =>
            Task.FromException<SubscribeResponse>(new UpnpTimeoutException(url, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)));

        var handle = await h.Client.SubscribeAsync(Service(), Entry(), CancellationToken.None);
        var lapsed = new TaskCompletionSource<SubscriptionLapseReason>();
        handle.Lapsed += r => lapsed.TrySetResult(r);

        (await lapsed.Task.WaitAsync(TimeSpan.FromSeconds(30))).Should().Be(SubscriptionLapseReason.RenewTransportError);
    }

    // ── AC-4.2.13 active close → UNSUBSCRIBE with level-above token ───────────────
    [Fact]
    [Trait("ac", "AC-4.2.13")]
    public async Task ActiveClose_Unsubscribes_WithAdapterLinkedToken_NotCancelledPopupToken()
    {
        using var adapterCts = new CancellationTokenSource();
        var h = NewHarness(NeverDelayAsync, adapterCts.Token);
        h.Http.SubscribeResponder = (_, _, _, _) => Task.FromResult(new SubscribeResponse("sid-close", TimeSpan.FromSeconds(300)));

        bool unsubTokenWasCancelled = true;
        h.Http.UnsubscribeResponder = (_, _, ct) =>
        {
            unsubTokenWasCancelled = ct.IsCancellationRequested; // must be FALSE
            return Task.CompletedTask;
        };

        using var popupCts = new CancellationTokenSource();
        var handle = await h.Client.SubscribeAsync(Service(), Entry(), popupCts.Token);

        // Simulate the popup-close: cancel the popup token THEN close. The UNSUBSCRIBE must still go out
        // over a fresh adapter-linked token (D7), not the cancelled popup token.
        await popupCts.CancelAsync();
        await handle.CloseAsync();

        h.Http.CountOf("UNSUBSCRIBE").Should().Be(1);
        unsubTokenWasCancelled.Should().BeFalse("UNSUBSCRIBE must use a FRESH CTS linked to the adapter token, not the cancelled popup token");
        adapterCts.IsCancellationRequested.Should().BeFalse("the level-above adapter token must NOT be cancelled by a popup close");
        h.Diag.Entries.Should().Contain(e => e.Category == DiagCategories.GenaUnsubscribe);
    }

    [Fact]
    [Trait("ac", "AC-4.2.13")]
    public async Task ActiveClose_UnsubscribeFails_Swallowed_EmitsFailed_StillCompletes()
    {
        var h = NewHarness(NeverDelayAsync);
        h.Http.SubscribeResponder = (_, _, _, _) => Task.FromResult(new SubscribeResponse("sid-uf", TimeSpan.FromSeconds(300)));
        h.Http.UnsubscribeResponder = (url, _, _) => Task.FromException(new UpnpTransportException(url, "boom", 500));

        var handle = await h.Client.SubscribeAsync(Service(), Entry(), CancellationToken.None);

        Func<Task> act = () => handle.CloseAsync();
        await act.Should().NotThrowAsync("a hung/failed UNSUBSCRIBE must not block close");
        h.Diag.Entries.Should().Contain(e => e.Category == DiagCategories.GenaUnsubscribeFailed);
    }

    [Fact]
    [Trait("ac", "AC-4.2.2")]
    public async Task CloseAsync_IsIdempotent_SecondCallNoOp()
    {
        var h = NewHarness(NeverDelayAsync);
        h.Http.SubscribeResponder = (_, _, _, _) => Task.FromResult(new SubscribeResponse("sid-idem", TimeSpan.FromSeconds(300)));
        var handle = await h.Client.SubscribeAsync(Service(), Entry(), CancellationToken.None);

        await handle.CloseAsync();
        await handle.CloseAsync();

        h.Http.CountOf("UNSUBSCRIBE").Should().Be(1, "only the first close acts");
    }

    // ── AC-4.2.15 adapter switch / device gone → lapse, no unsubscribe ───────────
    [Fact]
    [Trait("ac", "AC-4.2.15")]
    public async Task AdapterSwitch_Lapses_AdapterSwitch_NoUnsubscribe()
    {
        using var adapterCts = new CancellationTokenSource();
        var h = NewHarness(NeverDelayAsync, adapterCts.Token);
        h.Http.SubscribeResponder = (_, _, _, _) => Task.FromResult(new SubscribeResponse("sid-adp", TimeSpan.FromSeconds(300)));

        var handle = await h.Client.SubscribeAsync(Service(), Entry(), CancellationToken.None);
        var lapsed = new TaskCompletionSource<SubscriptionLapseReason>();
        handle.Lapsed += r => lapsed.TrySetResult(r);

        await adapterCts.CancelAsync();

        (await lapsed.Task.WaitAsync(TimeSpan.FromSeconds(30))).Should().Be(SubscriptionLapseReason.AdapterSwitch);
        h.Http.CountOf("UNSUBSCRIBE").Should().Be(0);
    }

    [Fact]
    [Trait("ac", "AC-4.2.15")]
    public async Task DeviceGone_Lapses_DeviceGone_NoUnsubscribe()
    {
        using var deviceCts = new CancellationTokenSource();
        var h = NewHarness(NeverDelayAsync);
        h.Http.SubscribeResponder = (_, _, _, _) => Task.FromResult(new SubscribeResponse("sid-dev", TimeSpan.FromSeconds(300)));

        // The entry's DeviceToken is derived from deviceCts (passed as the linked adapter token).
        var entry = Entry(deviceCts.Token);
        var handle = await h.Client.SubscribeAsync(Service(), entry, CancellationToken.None);
        var lapsed = new TaskCompletionSource<SubscriptionLapseReason>();
        handle.Lapsed += r => lapsed.TrySetResult(r);

        await deviceCts.CancelAsync();

        (await lapsed.Task.WaitAsync(TimeSpan.FromSeconds(30))).Should().Be(SubscriptionLapseReason.DeviceGone);
        h.Http.CountOf("UNSUBSCRIBE").Should().Be(0);
    }

    // ── AC-4.2.17 concurrent + independent ───────────────────────────────────────
    [Fact]
    [Trait("ac", "AC-4.2.17")]
    public async Task ThreeConcurrentSubs_OneRenewFails_OthersUnaffected()
    {
        // Immediate-fire delay; the handle REPLAYS a pre-attach lapse so sid-2's lapse is captured
        // deterministically. sid-2 refuses (412 → lapse + exit); sid-1/sid-3 succeed once then BLOCK on
        // the next renew (the responder parks), so they neither spin nor lapse — proving independence.
        var h = NewHarness((_, _) => Task.CompletedTask);
        var counter = 0;
        h.Http.SubscribeResponder = (_, _, _, _) =>
        {
            var n = Interlocked.Increment(ref counter);
            return Task.FromResult(new SubscribeResponse($"sid-{n}", TimeSpan.FromSeconds(300)));
        };
        var renewsBySid = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
        h.Http.RenewResponder = async (url, sid, _, ct) =>
        {
            if (sid == "sid-2")
            {
                throw new UpnpTransportException(url, "412", 412);
            }
            // First renew succeeds; subsequent renews park (so sid-1/sid-3 fire exactly once, no spin).
            if (renewsBySid.AddOrUpdate(sid, 1, (_, v) => v + 1) > 1)
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            return new SubscribeResponse(sid, TimeSpan.FromSeconds(300));
        };

        var entry = Entry();
        var h1 = await h.Client.SubscribeAsync(Service("s1"), entry, CancellationToken.None);
        var h2 = await h.Client.SubscribeAsync(Service("s2"), entry, CancellationToken.None);
        var h3 = await h.Client.SubscribeAsync(Service("s3"), entry, CancellationToken.None);

        var s2Lapsed = new TaskCompletionSource<SubscriptionLapseReason>();
        var s1Lapsed = false;
        var s3Lapsed = false;
        h2.Lapsed += r => s2Lapsed.TrySetResult(r);
        h1.Lapsed += _ => s1Lapsed = true;
        h3.Lapsed += _ => s3Lapsed = true;

        (await s2Lapsed.Task.WaitAsync(TimeSpan.FromSeconds(30))).Should().Be(SubscriptionLapseReason.RenewRefused);
        s1Lapsed.Should().BeFalse("sub 1 is independent of sub 2's failed renew");
        s3Lapsed.Should().BeFalse("sub 3 is independent of sub 2's failed renew");

        // sub 1 + 3 still route NOTIFYs.
        var got1 = new TaskCompletionSource();
        h1.NotificationReceived += _ => got1.TrySetResult();
        await h.Host.RaiseNotifyAsync(Notify("sid-1", Propertyset(("V", "1"))));
        await got1.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
            {
                throw new TimeoutException("condition not met in time");
            }
            await Task.Delay(5);
        }
    }
}
