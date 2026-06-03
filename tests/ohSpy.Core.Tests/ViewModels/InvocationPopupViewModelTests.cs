namespace ohSpy.Core.Tests.ViewModels;

using System.Diagnostics;
using FluentAssertions;
using ohSpy.Core.Devices;
using ohSpy.Core.Diagnostics;
using ohSpy.Core.Http;
using ohSpy.Core.Models;
using ohSpy.Core.Tests.Fakes;
using ohSpy.Core.ViewModels;

/// <summary>
/// Story 3.2 — <see cref="InvocationPopupViewModel"/> unit tests (the automated-test heart).
/// Asserts on the SoapRequest that WENT OUT (captured by the stub) and the result/diagnostic
/// the VM produced — NOT on inputs handed in (Epic 2 lesson).
/// </summary>
public sealed class InvocationPopupViewModelTests
{
    private static readonly Uri DeviceLocation = new("http://192.168.1.100:49152/desc.xml");
    private static readonly Guid DeviceUuid = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static ScpdArgument In(string name) => new(name, "Var", ScpdDirection.In);

    private static ScpdAction Action(string name, params string[] inputNames) =>
        new(name, inputNames.Select(In).ToList(), Array.Empty<ScpdArgument>());

    // ControlUrl relative by default — exercises reconciliation #1 (resolve against LocationUrl).
    private static ServiceDescription Service(
        string serviceType = "urn:schemas-upnp-org:service:RenderingControl:1",
        string controlUrl = "/RC/ctrl") =>
        new(serviceType, "urn:upnp-org:serviceId:RenderingControl", "/RC/Scpd.xml", controlUrl, "/RC/evt");

    private static RegistryEntry Entry(Guid? uuid = null, Uri? location = null, CancellationToken token = default) =>
        new(uuid ?? DeviceUuid, location ?? DeviceLocation, DateTime.UtcNow, token);

    private static InvocationPopupViewModel MakeVm(
        ScpdAction action,
        out StubUpnpHttpClient http,
        out CapturingDiagnosticEmitter diag,
        out FakeDeviceRegistry registry,
        ServiceDescription? service = null,
        RegistryEntry? entry = null)
    {
        http = new StubUpnpHttpClient();
        diag = new CapturingDiagnosticEmitter();
        registry = new FakeDeviceRegistry();
        return new InvocationPopupViewModel(
            action, service ?? Service(), entry ?? Entry(),
            http, new InlineUiDispatcher(), diag, registry);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition() && sw.ElapsedMilliseconds < timeoutMs)
            await Task.Delay(5);
        condition().Should().BeTrue($"the expected state was not reached within {timeoutMs}ms");
    }

    // ─── Title (AC-3.2.2 #4) ────────────────────────────────────────────────────

    [Fact]
    [Trait("ac", "AC-3.2.2")]
    public void Title_IsServiceTailDotAction_AC322()
    {
        var vm = MakeVm(Action("SetVolume"), out _, out _, out _);

        vm.Title.Should().Be("RenderingControl:1 · SetVolume");
    }

    // ─── Inputs population (AC-3.2.2 #5 / AC-3.2.5) ──────────────────────────────

    [Fact]
    [Trait("ac", "AC-3.2.2")]
    public void Inputs_PopulatedInDeclaredOrder_AC322()
    {
        var vm = MakeVm(Action("SetVolume", "Channel", "DesiredVolume"), out _, out _, out _);

        vm.Inputs.Select(i => i.Name).Should().Equal("Channel", "DesiredVolume");
    }

    [Fact]
    [Trait("ac", "AC-3.2.5")]
    public void Inputs_ArgumentLessAction_IsEmpty_CanInvoke_AC325()
    {
        var vm = MakeVm(Action("GetVolume"), out _, out _, out _);

        vm.Inputs.Should().BeEmpty();
        vm.InvokeCommand.CanExecute(null).Should().BeTrue("argument-less actions are invokable");
    }

    // ─── SoapRequest construction incl. relative-ControlUrl resolution (AC-3.2.6) ─

    [Fact]
    [Trait("ac", "AC-3.2.6")]
    public async Task Invoke_BuildsSoapRequest_ResolvesRelativeControlUrl_MapsArgs_AC326()
    {
        var vm = MakeVm(Action("SetVolume", "Channel", "DesiredVolume"),
            out var http, out _, out _);
        http.InvokeResponder = (_, _) => Task.FromResult(new SoapResponse("SetVolume", Array.Empty<SoapArgument>()));
        vm.Inputs[0].Value = "Master";
        vm.Inputs[1].Value = "30";

        await vm.InvokeCommand.ExecuteAsync(null);

        var req = http.InvokedRequests.Should().ContainSingle().Subject;
        req.ControlUrl.Should().Be(new Uri("http://192.168.1.100:49152/RC/ctrl"),
            "the relative ControlUrl resolves against the device LocationUrl");
        req.ServiceType.Should().Be("urn:schemas-upnp-org:service:RenderingControl:1");
        req.ActionName.Should().Be("SetVolume");
        req.InputArguments.Select(a => (a.Name, a.Value))
            .Should().Equal(("Channel", "Master"), ("DesiredVolume", "30"));
    }

    [Fact]
    [Trait("ac", "AC-3.2.6")]
    public async Task Invoke_AbsoluteControlUrl_PassesThrough_AC326()
    {
        var vm = MakeVm(Action("GetVolume"), out var http, out _, out _,
            service: Service(controlUrl: "http://10.0.0.9/ctrl"));
        http.InvokeResponder = (_, _) => Task.FromResult(new SoapResponse("GetVolume", Array.Empty<SoapArgument>()));

        await vm.InvokeCommand.ExecuteAsync(null);

        http.InvokedRequests.Should().ContainSingle()
            .Which.ControlUrl.Should().Be(new Uri("http://10.0.0.9/ctrl"));
    }

    [Fact]
    [Trait("ac", "AC-3.2.6")]
    public async Task Invoke_MalformedControlUrl_ShortCircuitsToTransportError_NoSoapCall_AC326()
    {
        // A relative ControlUrl against a location is normally resolvable; force failure by making
        // the ControlUrl an invalid absolute-looking string the resolver can't combine.
        var vm = MakeVm(Action("GetVolume"), out var http, out _, out _,
            service: Service(controlUrl: "http://[::bad"));
        http.InvokeResponder = (_, _) => Task.FromResult(new SoapResponse("GetVolume", Array.Empty<SoapArgument>()));

        await vm.InvokeCommand.ExecuteAsync(null);

        vm.Result.Should().BeOfType<TransportErrorResult>();
        http.InvokedRequests.Should().BeEmpty("a malformed control URL must not make a SOAP call");
    }

    // ─── Success result (AC-3.2.7) ──────────────────────────────────────────────

    [Fact]
    [Trait("ac", "AC-3.2.7")]
    public async Task Invoke_Success_SetsSuccessResultWithOutputs_AC327()
    {
        var outputs = new[] { new SoapArgument("CurrentVolume", "30") };
        var vm = MakeVm(Action("GetVolume"), out var http, out _, out _);
        http.InvokeResponder = (_, _) => Task.FromResult(new SoapResponse("GetVolume", outputs));

        await vm.InvokeCommand.ExecuteAsync(null);

        vm.Result.Should().BeOfType<SuccessResult>()
            .Which.Outputs.Should().BeEquivalentTo(outputs);
    }

    [Fact]
    [Trait("ac", "AC-3.2.7")]
    public async Task Invoke_Success_NoOutput_EmptyOutputs_AC327()
    {
        var vm = MakeVm(Action("SetVolume"), out var http, out _, out _);
        http.InvokeResponder = (_, _) => Task.FromResult(new SoapResponse("SetVolume", Array.Empty<SoapArgument>()));

        await vm.InvokeCommand.ExecuteAsync(null);

        vm.Result.Should().BeOfType<SuccessResult>().Which.Outputs.Should().BeEmpty();
    }

    // ─── Threading: terminal state is marshalled through IUiDispatcher (AC-3.2.7) ─

    [Fact]
    [Trait("ac", "AC-3.2.7")]
    public async Task Invoke_Success_MarshalsTerminalStateThroughDispatcher_NotDirectly_AC327()
    {
        // Regression for the Story 3.2 smoke crash (2026-06-03): the post-await continuation runs on
        // a thread-pool thread (WinUI 3 installs no SynchronizationContext for ConfigureAwait to
        // capture). The VM MUST marshal Result/IsInvoking via IUiDispatcher, or the bound window
        // pokes UIElement.Visibility off-thread → COMException 0x8001010E (RPC_E_WRONGTHREAD) →
        // process crash. InlineUiDispatcher (used by the other tests) runs Post inline and so MASKS
        // this — a deferred dispatcher proves the terminal state is applied through Post(), not by a
        // direct off-thread assignment.
        var ui = new DeferredUiDispatcher();
        var http = new StubUpnpHttpClient();
        var diag = new CapturingDiagnosticEmitter();
        var registry = new FakeDeviceRegistry();
        var vm = new InvocationPopupViewModel(
            Action("GetVolume"), Service(), Entry(), http, ui, diag, registry);
        http.InvokeResponder = (_, _) => Task.FromResult(new SoapResponse("GetVolume", Array.Empty<SoapArgument>()));

        await vm.InvokeCommand.ExecuteAsync(null);

        // Before the dispatcher drains, the terminal state must NOT be applied — proving it went
        // through Post rather than a direct (would-be off-thread) assignment.
        vm.Result.Should().BeNull("the success result must be marshalled through the UI dispatcher, not set directly");
        vm.IsInvoking.Should().BeTrue("IsInvoking=false is part of the marshalled terminal state");
        ui.PostCount.Should().BeGreaterThan(0, "the VM must Post its terminal UI mutation");

        ui.Drain();

        vm.Result.Should().BeOfType<SuccessResult>();
        vm.IsInvoking.Should().BeFalse();
    }

    // ─── Fault result + diagnostic (AC-3.2.8 / AC-3.2.12) ────────────────────────

    [Fact]
    [Trait("ac", "AC-3.2.8")]
    public async Task Invoke_UpnpFault_SetsFaultResult_EmitsSoapFaultWithUuid_AC328()
    {
        var url = new Uri("http://192.168.1.100:49152/RC/ctrl");
        var vm = MakeVm(Action("SetVolume"), out var http, out var diag, out _);
        http.InvokeResponder = (_, _) =>
            throw new UpnpFaultException(url, "SetVolume", 402, "Invalid Args");

        await vm.InvokeCommand.ExecuteAsync(null);

        var fault = vm.Result.Should().BeOfType<FaultResult>().Subject;
        fault.StatusCode.Should().Be(500);
        fault.ErrorCode.Should().Be(402);
        fault.ErrorDescription.Should().Be("Invalid Args");

        var entry = diag.Entries.Should().ContainSingle().Subject;
        entry.Severity.Should().Be("Warning");
        entry.Category.Should().Be(DiagCategories.SoapFault);
        entry.Context.DeviceUuid.Should().Be(DeviceUuid, "the popup emit carries the UUID (reconciliation #4)");
        entry.Context.ActionName.Should().Be("SetVolume");
        entry.Context.StatusCode.Should().Be(500);
        entry.Context.ErrorText.Should().Be("402: Invalid Args");
    }

    // ─── Transport-error result + diagnostic (AC-3.2.9 / AC-3.2.12) ──────────────

    [Fact]
    [Trait("ac", "AC-3.2.9")]
    public async Task Invoke_Timeout_SetsTransportError_EmitsHttpTimeoutWithUuid_AC329()
    {
        var url = new Uri("http://192.168.1.100:49152/RC/ctrl");
        var vm = MakeVm(Action("GetVolume"), out var http, out var diag, out _);
        http.InvokeResponder = (_, _) =>
            throw new UpnpTimeoutException(url, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(6));

        await vm.InvokeCommand.ExecuteAsync(null);

        vm.Result.Should().BeOfType<TransportErrorResult>();
        var entry = diag.Entries.Should().ContainSingle().Subject;
        entry.Category.Should().Be(DiagCategories.HttpTimeout);
        entry.Context.DeviceUuid.Should().Be(DeviceUuid);
        entry.Context.ActionName.Should().Be("GetVolume");
        entry.Context.Elapsed.Should().Be(TimeSpan.FromSeconds(6));
        entry.Context.Budget.Should().Be(TimeSpan.FromSeconds(5));
    }

    [Fact]
    [Trait("ac", "AC-3.2.9")]
    public async Task Invoke_Transport_SetsTransportError_EmitsSoapInvokeWithUuid_AC329()
    {
        var url = new Uri("http://192.168.1.100:49152/RC/ctrl");
        var vm = MakeVm(Action("GetVolume"), out var http, out var diag, out _);
        http.InvokeResponder = (_, _) =>
            throw new UpnpTransportException(url, "connection refused", statusCode: 503);

        await vm.InvokeCommand.ExecuteAsync(null);

        vm.Result.Should().BeOfType<TransportErrorResult>();
        var entry = diag.Entries.Should().ContainSingle().Subject;
        entry.Category.Should().Be(DiagCategories.SoapInvoke);
        entry.Context.DeviceUuid.Should().Be(DeviceUuid);
        entry.Context.StatusCode.Should().Be(503);
    }

    [Fact]
    [Trait("ac", "AC-3.2.9")]
    public async Task Invoke_Protocol_SetsTransportError_EmitsSoapInvoke_AC329()
    {
        var url = new Uri("http://192.168.1.100:49152/RC/ctrl");
        var vm = MakeVm(Action("GetVolume"), out var http, out var diag, out _);
        http.InvokeResponder = (_, _) => throw new UpnpProtocolException(url, "malformed 2xx body");

        await vm.InvokeCommand.ExecuteAsync(null);

        vm.Result.Should().BeOfType<TransportErrorResult>();
        diag.Entries.Should().ContainSingle().Which.Category.Should().Be(DiagCategories.SoapInvoke);
    }

    // ─── In-flight guard (AC-3.2.6 #19 / AC-3.2.2 #7) ────────────────────────────

    [Fact]
    [Trait("ac", "AC-3.2.6")]
    public async Task Invoke_InFlight_IsInvokingTrue_CanInvokeFalse_AC326()
    {
        var gate = new TaskCompletionSource();
        var vm = MakeVm(Action("GetVolume"), out var http, out _, out _);
        http.InvokeResponder = async (_, ct) =>
        {
            await gate.Task.WaitAsync(ct);
            return new SoapResponse("GetVolume", Array.Empty<SoapArgument>());
        };

        var invoke = vm.InvokeCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => vm.IsInvoking);

        vm.IsInvoking.Should().BeTrue();
        vm.InvokeCommand.CanExecute(null).Should().BeFalse("re-invoke guard while a call is in flight");

        gate.SetResult();
        await invoke;
        vm.IsInvoking.Should().BeFalse();
        vm.InvokeCommand.CanExecute(null).Should().BeTrue();
    }

    // ─── Cancel-on-dispose swallows OCE, no diagnostic, no Result (AC-3.2.10) ─────

    [Fact]
    [Trait("ac", "AC-3.2.10")]
    public async Task Dispose_MidInvoke_CancelsSwallowsOce_NoResult_NoDiagnostic_AC3210()
    {
        var started = new TaskCompletionSource();
        var vm = MakeVm(Action("GetVolume"), out var http, out var diag, out _);
        http.InvokeResponder = async (_, ct) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct); // blocks until popup-close cancels the token
            return new SoapResponse("GetVolume", Array.Empty<SoapArgument>());
        };

        var invoke = vm.InvokeCommand.ExecuteAsync(null);
        await started.Task;
        vm.Dispose(); // popup close → _popupCts.Cancel()
        await invoke; // must complete (OCE swallowed), not throw

        vm.Result.Should().BeNull("cancellation is not a fault — no result");
        diag.Entries.Should().BeEmpty("cancellation emits no diagnostic");
        vm.IsInvoking.Should().BeFalse();
    }

    // ─── FR-037 DeviceRemoved banner on UUID match (AC-3.2.11) ───────────────────

    [Fact]
    [Trait("ac", "AC-3.2.11")]
    public void DeviceRemoved_UuidMatch_FlipsBanner_AC3211()
    {
        var vm = MakeVm(Action("GetVolume"), out _, out _, out var registry);

        registry.RaiseDeviceRemoved(DeviceUuid);

        vm.IsDeviceGone.Should().BeTrue();
        vm.DeviceGoneText.Should().NotBeEmpty();
    }

    [Fact]
    [Trait("ac", "AC-3.2.11")]
    public void DeviceRemoved_DifferentUuid_NoBanner_AC3211()
    {
        var vm = MakeVm(Action("GetVolume"), out _, out _, out var registry);

        registry.RaiseDeviceRemoved(Guid.NewGuid());

        vm.IsDeviceGone.Should().BeFalse();
    }

    [Fact]
    [Trait("ac", "AC-3.2.11")]
    public async Task DeviceRemoved_MidInvoke_CancelsViaLinkedToken_SwallowsOce_AC3211()
    {
        using var deviceCts = new CancellationTokenSource();
        var entry = Entry(token: deviceCts.Token);
        var started = new TaskCompletionSource();
        var http = new StubUpnpHttpClient();
        var diag = new CapturingDiagnosticEmitter();
        var registry = new FakeDeviceRegistry();
        var vm = new InvocationPopupViewModel(
            Action("GetVolume"), Service(), entry, http, new InlineUiDispatcher(), diag, registry);
        http.InvokeResponder = async (_, ct) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return new SoapResponse("GetVolume", Array.Empty<SoapArgument>());
        };

        var invoke = vm.InvokeCommand.ExecuteAsync(null);
        await started.Task;
        await deviceCts.CancelAsync(); // device removed → device token → linked popup token cancels
        await invoke;

        vm.Result.Should().BeNull("the in-flight invocation was cancelled — not a fault");
        diag.Entries.Should().BeEmpty();
    }

    // ─── Dispose: unsubscribes + idempotent (AC-3.2.10 #28) ──────────────────────

    [Fact]
    [Trait("ac", "AC-3.2.10")]
    public void Dispose_UnsubscribesFromRegistry_NoBannerAfterDispose_AC3210()
    {
        var vm = MakeVm(Action("GetVolume"), out _, out _, out var registry);

        vm.Dispose();
        registry.RaiseDeviceRemoved(DeviceUuid); // handler already removed

        vm.IsDeviceGone.Should().BeFalse("Dispose unsubscribed from DeviceRemoved");
    }

    [Fact]
    [Trait("ac", "AC-3.2.10")]
    public void Dispose_IsIdempotent_AC3210()
    {
        var vm = MakeVm(Action("GetVolume"), out _, out _, out _);

        var act = () => { vm.Dispose(); vm.Dispose(); };

        act.Should().NotThrow("Interlocked guard makes Dispose idempotent");
    }
}
