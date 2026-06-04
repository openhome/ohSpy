namespace ohSpy.Core.Tests.ViewModels;

using System.Diagnostics;
using FluentAssertions;
using ohSpy.Core.Devices;
using ohSpy.Core.Events;
using ohSpy.Core.Models;
using ohSpy.Core.Tests.Fakes;
using ohSpy.Core.Threading;
using ohSpy.Core.ViewModels;
using Xunit;

/// <summary>
/// Story 5.2 — AC-5.2.5 / AC-5.2.11 popup-cascade. On adapter switch, EVERY open popup must transition
/// to its FR-037 device-unreachable state without crashing or blocking the 2 s sequence:
/// Properties (2.9) + Invocation (3.2) via <c>DeviceRemoved</c> (raised by <c>DeviceRegistry.Clear()</c>),
/// and Subscription (4.3) via BOTH <c>DeviceRemoved</c> AND <c>handle.Lapsed(AdapterSwitch)</c> (the 4.2
/// renew loop's <c>_adapterToken.Register(() =&gt; Lapse(AdapterSwitch))</c>, fired by step-1's
/// <c>_adapterCts.Cancel()</c>). Driven at the Core VM level against the REAL registry + REAL
/// subscription client (the App windows are App-only → manual smoke).
/// </summary>
public sealed class AdapterSwitchPopupCascadeTests
{
    private static readonly Uri DeviceLocation = new("http://192.168.1.100:49152/desc.xml");

    private static ServiceDescription Service() =>
        new("urn:schemas-upnp-org:service:AVTransport:1", "urn:upnp-org:serviceId:AVTransport",
            "/AVT/Scpd.xml", "/AVT/ctrl", "/AVT/evt");

    private static ScpdAction Action() =>
        new("Play", new List<ScpdArgument>(), new List<ScpdArgument>());

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition() && sw.ElapsedMilliseconds < timeoutMs)
            await Task.Delay(5);
        condition().Should().BeTrue($"the expected state was not reached within {timeoutMs}ms");
    }

    // The DeviceRemoved-driven cascade: Clear() flips Properties + Invocation banners (AC-5.2.5).
    [Fact]
    [Trait("ac", "AC-5.2.5")]
    [Trait("ac", "AC-5.2.11")]
    public void Clear_FlipsPropertiesAndInvocationPopupsToDeviceGone()
    {
        var ui = new InlineUiDispatcher();
        var registry = new DeviceRegistry(ui);
        var uuid = Guid.NewGuid();
        registry.OnAlive(uuid, DeviceLocation, DateTime.UtcNow, "S", null, null, null, CancellationToken.None);
        registry.TryGetEntry(uuid, out var entry).Should().BeTrue();
        entry.MarkInFlight();
        entry.MarkLoaded(StubDeviceDescriptionParser.Description($"uuid:{uuid}"));

        var properties = new PropertiesViewModel(entry, registry, new FakeUriLauncher(), new CapturingDiagnosticEmitter());
        var invocation = new InvocationPopupViewModel(
            Action(), Service(), entry, new StubUpnpHttpClient(), ui,
            new CapturingDiagnosticEmitter(), registry, new StubScpdParser());

        properties.IsDeviceGone.Should().BeFalse();
        invocation.IsDeviceGone.Should().BeFalse();

        // Step 6 of the switch: registry cleared → DeviceRemoved per UUID.
        registry.Clear();

        properties.IsDeviceGone.Should().BeTrue("Properties flips on DeviceRemoved (FR-037)");
        invocation.IsDeviceGone.Should().BeTrue("Invocation flips on DeviceRemoved (FR-037)");

        properties.Dispose();
        invocation.Dispose();
    }

    // The subscription popup reaches device-unreachable via BOTH the adapter-token cancel
    // (handle.Lapsed(AdapterSwitch)) AND DeviceRemoved — convergent + idempotent (AC-5.2.11).
    [Fact]
    [Trait("ac", "AC-5.2.5")]
    [Trait("ac", "AC-5.2.11")]
    public async Task AdapterSwitch_FlipsSubscriptionPopupToUnreachable_ViaLapseAndDeviceRemoved()
    {
        var ui = new InlineUiDispatcher();
        var registry = new DeviceRegistry(ui);

        // A real adapter CTS — the entry's DeviceCts links to it, and the subscription client's renew
        // loop registers a Lapse(AdapterSwitch) callback on it. Cancelling it IS the switch's step 1.
        using var adapterCts = new CancellationTokenSource();
        var uuid = Guid.NewGuid();
        registry.OnAlive(uuid, DeviceLocation, DateTime.UtcNow, "S", null, null, null, adapterCts.Token);
        registry.TryGetEntry(uuid, out var entry).Should().BeTrue();
        entry.MarkInFlight();
        entry.MarkLoaded(StubDeviceDescriptionParser.Description($"uuid:{uuid}"));

        // Real subscription client (never-firing renew delay), bound to the adapter token + a fake host.
        var http = new StubUpnpHttpClient
        {
            SubscribeResponder = (_, _, _, _) =>
                Task.FromResult(new ohSpy.Core.Http.SubscribeResponse("uuid:sub-1", TimeSpan.FromSeconds(300))),
        };
        var client = new SubscriptionClient(http, new CapturingDiagnosticEmitter(),
            static (_, ct) => Task.Delay(Timeout.Infinite, ct));
        client.SetCallbackHost(new FakeEventCallbackHost());
        client.SetAdapterContext(adapterCts.Token);

        var vm = new SubscriptionPopupViewModel(
            Service(), entry, client, ui, new CapturingDiagnosticEmitter(), registry);
        await vm.InitializeAsync(); // SUBSCRIBE succeeds (the stub grants a SID) → renew loop arms the adapter callback
        vm.Status.Should().Be(SubscriptionStatus.Subscribed);

        // Switch step 1: cancel the adapter token → Lapse(AdapterSwitch) cascades into the live sub.
        await adapterCts.CancelAsync();
        await WaitUntilAsync(() => vm.Status is SubscriptionStatus.Lapsed or SubscriptionStatus.DeviceGone);

        // Switch step 6: registry cleared → DeviceRemoved (the convergent path).
        registry.Clear();

        vm.Status.Should().BeOneOf(SubscriptionStatus.Lapsed, SubscriptionStatus.DeviceGone);

        vm.Dispose();
    }
}
