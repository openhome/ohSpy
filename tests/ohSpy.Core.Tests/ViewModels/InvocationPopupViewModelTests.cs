namespace ohSpy.Core.Tests.ViewModels;

using System.Diagnostics;
using FluentAssertions;
using ohSpy.Core.Devices;
using ohSpy.Core.Diagnostics;
using ohSpy.Core.Http;
using ohSpy.Core.Models;
using ohSpy.Core.Tests.Fakes;
using ohSpy.Core.Threading;
using ohSpy.Core.ViewModels;

/// <summary>
/// Story 3.2 — <see cref="InvocationPopupViewModel"/> unit tests (the automated-test heart).
/// Asserts on the SoapRequest that WENT OUT (captured by the stub) and the result/diagnostic
/// the VM produced — NOT on inputs handed in (Epic 2 lesson).
/// </summary>
public sealed class InvocationPopupViewModelTests
{
    private static readonly Uri DeviceLocation = new("http://192.168.1.100:49152/desc.xml");
    private const string DeviceUdn = "uuid:22222222-2222-2222-2222-222222222222";

    private static ScpdArgument In(string name) => new(name, "Var", ScpdDirection.In);

    private static ScpdAction Action(string name, params string[] inputNames) =>
        new(name, inputNames.Select(In).ToList(), Array.Empty<ScpdArgument>());

    // ControlUrl relative by default — exercises reconciliation #1 (resolve against LocationUrl).
    private static ServiceDescription Service(
        string serviceType = "urn:schemas-upnp-org:service:RenderingControl:1",
        string controlUrl = "/RC/ctrl") =>
        new(serviceType, "urn:upnp-org:serviceId:RenderingControl", "/RC/Scpd.xml", controlUrl, "/RC/evt");

    private static RegistryEntry Entry(string? udn = null, Uri? location = null, CancellationToken token = default) =>
        new(udn ?? DeviceUdn, location ?? DeviceLocation, DateTime.UtcNow, token);

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
            http, new InlineUiDispatcher(), diag, registry, new StubScpdParser());
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
            Action("GetVolume"), Service(), Entry(), http, ui, diag, registry, new StubScpdParser());
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
        entry.Context.DeviceUuid.Should().Be(DeviceUdn, "the popup emit carries the UUID (reconciliation #4)");
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
        entry.Context.DeviceUuid.Should().Be(DeviceUdn);
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
        entry.Context.DeviceUuid.Should().Be(DeviceUdn);
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

        registry.RaiseDeviceRemoved(DeviceUdn);

        vm.IsDeviceGone.Should().BeTrue();
        vm.DeviceGoneText.Should().NotBeEmpty();
    }

    [Fact]
    [Trait("ac", "AC-3.2.11")]
    public void DeviceRemoved_DifferentUuid_NoBanner_AC3211()
    {
        var vm = MakeVm(Action("GetVolume"), out _, out _, out var registry);

        registry.RaiseDeviceRemoved($"uuid:{Guid.NewGuid()}");

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
            Action("GetVolume"), Service(), entry, http, new InlineUiDispatcher(), diag, registry, new StubScpdParser());
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
        registry.RaiseDeviceRemoved(DeviceUdn); // handler already removed

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

    // ════════════════════════════════════════════════════════════════════════════
    //  Story 3.3 — InitializeAsync: SCPD state-table load + constrained-input resolution
    // ════════════════════════════════════════════════════════════════════════════

    private static readonly byte[] DummyScpdBytes = System.Text.Encoding.UTF8.GetBytes("<scpd/>");

    // An input arg whose RelatedStateVariable matches a state-table key.
    private static ScpdArgument InVar(string argName, string relatedStateVariable) =>
        new(argName, relatedStateVariable, ScpdDirection.In);

    private static ScpdAction ActionWith(string name, params ScpdArgument[] inputs) =>
        new(name, inputs.ToList(), Array.Empty<ScpdArgument>());

    private static ScpdStateVariable Sv(
        string name, string dataType, string? defaultValue = null,
        IReadOnlyList<string>? allowedValueList = null, ScpdAllowedValueRange? allowedValueRange = null) =>
        new(name, dataType, defaultValue, allowedValueList, allowedValueRange);

    private static ScpdStateTable Table(params ScpdStateVariable[] vars) =>
        new(vars.ToDictionary(v => v.Name, StringComparer.Ordinal));

    // params helper avoids CA1861 (constant array argument) at the call sites.
    private static string[] L(params string[] values) => values;

    // Builds a VM wired so InitializeAsync fetches DummyScpdBytes and parses to the supplied table.
    private static InvocationPopupViewModel MakeInitVm(
        ScpdAction action,
        ScpdStateTable table,
        out StubUpnpHttpClient http,
        out CapturingDiagnosticEmitter diag,
        IUiDispatcher? ui = null,
        Func<Exception>? parseThrower = null,
        Func<Uri, CancellationToken, Task<byte[]>>? scpdResponder = null)
    {
        http = new StubUpnpHttpClient
        {
            ScpdResponder = scpdResponder ?? ((_, _) => Task.FromResult(DummyScpdBytes)),
        };
        diag = new CapturingDiagnosticEmitter();
        var registry = new FakeDeviceRegistry();
        var parser = new StubScpdParser { StateTable = table, StateTableThrower = parseThrower };
        return new InvocationPopupViewModel(
            action, Service(), Entry(), http, ui ?? new InlineUiDispatcher(), diag, registry, parser);
    }

    [Fact]
    [Trait("ac", "AC-3.3.2")]
    public async Task InitializeAsync_ListVariable_ResolvesListVariant_FR102()
    {
        var action = ActionWith("SetMode", InVar("DesiredMode", "Mode"));
        var table = Table(Sv("Mode", "string", "Stereo", allowedValueList: L("Stereo", "Mono", "Surround")));
        var vm = MakeInitVm(action, table, out _, out var diag);

        await vm.InitializeAsync();

        var list = vm.Inputs.Should().ContainSingle().Subject
            .Should().BeOfType<AllowedValueListArgumentViewModel>().Subject;
        list.AllowedValues.Should().Equal("Stereo", "Mono", "Surround");
        list.SelectedValue.Should().Be("Stereo");
        vm.IsLoadingInputs.Should().BeFalse();
        diag.Entries.Should().BeEmpty("a well-formed list emits no diagnostic");
    }

    [Fact]
    [Trait("ac", "AC-3.3.4")]
    public async Task InitializeAsync_RangeVariable_ResolvesRangeVariant_FR103()
    {
        var action = ActionWith("SetVolume", InVar("DesiredVolume", "Volume"));
        var table = Table(Sv("Volume", "ui4", "50",
            allowedValueRange: new ScpdAllowedValueRange(0, 100, 1)));
        var vm = MakeInitVm(action, table, out _, out var diag);

        await vm.InitializeAsync();

        var range = vm.Inputs.Should().ContainSingle().Subject
            .Should().BeOfType<AllowedValueRangeArgumentViewModel>().Subject;
        range.Minimum.Should().Be(0);
        range.Maximum.Should().Be(100);
        range.Step.Should().Be(1);
        range.NumericValue.Should().Be(50);
        diag.Entries.Should().BeEmpty();
    }

    [Fact]
    [Trait("ac", "AC-3.3.4")]
    public async Task InitializeAsync_RangeVariable_NoStep_ResolvesRangeVariant_FR103()
    {
        var action = ActionWith("SetBalance", InVar("DesiredBalance", "Balance"));
        var table = Table(Sv("Balance", "i4",
            allowedValueRange: new ScpdAllowedValueRange(-15, 15, null)));
        var vm = MakeInitVm(action, table, out _, out _);

        await vm.InitializeAsync();

        var range = vm.Inputs.Should().ContainSingle().Subject
            .Should().BeOfType<AllowedValueRangeArgumentViewModel>().Subject;
        range.Step.Should().BeNull();
        range.NumericValue.Should().Be(-15, "no default → Minimum");
    }

    [Fact]
    [Trait("ac", "AC-3.3.3")]
    public async Task InitializeAsync_EmptyList_FallsBackToText_EmitsScpdParse_FR102()
    {
        var action = ActionWith("SetMode", InVar("DesiredMode", "Mode"));
        var table = Table(Sv("Mode", "string", allowedValueList: Array.Empty<string>()));
        var vm = MakeInitVm(action, table, out _, out var diag);

        await vm.InitializeAsync();

        vm.Inputs.Should().ContainSingle().Which.Should().BeOfType<ArgumentInputViewModel>("an empty list falls back to free-form text");
        diag.Entries.Should().ContainSingle().Which.Category.Should().Be(DiagCategories.ScpdParse);
    }

    [Fact]
    [Trait("ac", "AC-3.3.7")]
    public async Task InitializeAsync_RangeOnNonNumericType_FallsBackToText_EmitsScpdParse_FR103()
    {
        var action = ActionWith("SetX", InVar("X", "StrRange"));
        var table = Table(Sv("StrRange", "string",
            allowedValueRange: new ScpdAllowedValueRange(0, 10, 1)));
        var vm = MakeInitVm(action, table, out _, out var diag);

        await vm.InitializeAsync();

        vm.Inputs.Should().ContainSingle().Which.Should().BeOfType<ArgumentInputViewModel>();
        diag.Entries.Should().ContainSingle().Which.Category.Should().Be(DiagCategories.ScpdParse);
    }

    [Fact]
    [Trait("ac", "AC-3.3.7")]
    public async Task InitializeAsync_RangeMinGreaterThanMax_FallsBackToText_EmitsScpdParse_FR103()
    {
        var action = ActionWith("SetX", InVar("X", "BadRange"));
        var table = Table(Sv("BadRange", "ui4",
            allowedValueRange: new ScpdAllowedValueRange(100, 0, 1)));
        var vm = MakeInitVm(action, table, out _, out var diag);

        await vm.InitializeAsync();

        vm.Inputs.Should().ContainSingle().Which.Should().BeOfType<ArgumentInputViewModel>();
        diag.Entries.Should().ContainSingle().Which.Category.Should().Be(DiagCategories.ScpdParse);
    }

    [Fact]
    [Trait("ac", "AC-3.3.7")]
    public async Task InitializeAsync_RangeStepZero_FallsBackToText_EmitsScpdParse_FR103()
    {
        var action = ActionWith("SetX", InVar("X", "ZeroStep"));
        var table = Table(Sv("ZeroStep", "ui4",
            allowedValueRange: new ScpdAllowedValueRange(0, 100, 0)));
        var vm = MakeInitVm(action, table, out _, out var diag);

        await vm.InitializeAsync();

        vm.Inputs.Should().ContainSingle().Which.Should().BeOfType<ArgumentInputViewModel>();
        diag.Entries.Should().ContainSingle().Which.Category.Should().Be(DiagCategories.ScpdParse);
    }

    [Fact]
    [Trait("ac", "AC-3.3.8")]
    public async Task InitializeAsync_BothListAndRange_ListWins_EmitsScpdParse_FR102()
    {
        var action = ActionWith("SetMode", InVar("DesiredMode", "Both"));
        var table = Table(Sv("Both", "ui4", "Mono",
            allowedValueList: L("Stereo", "Mono"),
            allowedValueRange: new ScpdAllowedValueRange(0, 100, 1)));
        var vm = MakeInitVm(action, table, out _, out var diag);

        await vm.InitializeAsync();

        var list = vm.Inputs.Should().ContainSingle().Subject
            .Should().BeOfType<AllowedValueListArgumentViewModel>("FR-102 wins when both are declared").Subject;
        list.SelectedValue.Should().Be("Mono");
        diag.Entries.Should().ContainSingle().Which.Category.Should().Be(DiagCategories.ScpdParse);
    }

    [Fact]
    [Trait("ac", "AC-3.3.9")]
    public async Task InitializeAsync_NeitherConstraint_StaysText_NoDiagnostic()
    {
        var action = ActionWith("SetX", InVar("X", "Plain"));
        var table = Table(Sv("Plain", "string", "hello"));
        var vm = MakeInitVm(action, table, out _, out var diag);

        await vm.InitializeAsync();

        vm.Inputs.Should().ContainSingle().Which.Should().BeOfType<ArgumentInputViewModel>();
        diag.Entries.Should().BeEmpty("a plain variable is not malformed — no diagnostic");
    }

    [Fact]
    [Trait("ac", "AC-3.3.9")]
    public async Task InitializeAsync_RelatedVariableNotFound_StaysText_NoDiagnostic()
    {
        var action = ActionWith("SetX", InVar("X", "Missing"));
        var table = Table(Sv("SomethingElse", "string"));
        var vm = MakeInitVm(action, table, out _, out var diag);

        await vm.InitializeAsync();

        vm.Inputs.Should().ContainSingle().Which.Should().BeOfType<ArgumentInputViewModel>();
        diag.Entries.Should().BeEmpty("a name miss is legitimate, not malformed");
    }

    [Fact]
    [Trait("ac", "AC-3.3.1")]
    public async Task InitializeAsync_MixedInputs_ResolvesEachIndependently()
    {
        var action = ActionWith("DoLots",
            InVar("A", "Mode"),       // list
            InVar("B", "Volume"),     // range
            InVar("C", "Plain"),      // text
            InVar("D", "Missing"));   // text (not found)
        var table = Table(
            Sv("Mode", "string", "Mono", allowedValueList: L("Stereo", "Mono")),
            Sv("Volume", "ui4", "50", allowedValueRange: new ScpdAllowedValueRange(0, 100, 1)),
            Sv("Plain", "string"));
        var vm = MakeInitVm(action, table, out _, out _);

        await vm.InitializeAsync();

        vm.Inputs.Should().HaveCount(4);
        vm.Inputs[0].Should().BeOfType<AllowedValueListArgumentViewModel>();
        vm.Inputs[1].Should().BeOfType<AllowedValueRangeArgumentViewModel>();
        vm.Inputs[2].Should().BeOfType<ArgumentInputViewModel>();
        vm.Inputs[3].Should().BeOfType<ArgumentInputViewModel>();
        vm.Inputs.Select(i => i.Name).Should().Equal(L("A", "B", "C", "D"), "order preserved");
    }

    [Fact]
    [Trait("ac", "AC-3.3.1")]
    public async Task InitializeAsync_FetchFails_AllStayText_EmitsOneScpdParse()
    {
        var action = ActionWith("SetMode", InVar("DesiredMode", "Mode"));
        var table = Table(Sv("Mode", "string", allowedValueList: L("Stereo")));
        var vm = MakeInitVm(action, table, out _, out var diag,
            scpdResponder: (url, _) => throw new UpnpTransportException(url, "boom", statusCode: 500));

        await vm.InitializeAsync();

        vm.Inputs.Should().ContainSingle().Which.Should().BeOfType<ArgumentInputViewModel>("a fetch failure keeps the ctor's text inputs");
        vm.IsLoadingInputs.Should().BeFalse();
        var entry = diag.Entries.Should().ContainSingle().Subject;
        entry.Category.Should().Be(DiagCategories.ScpdParse);
        entry.Context.DeviceUuid.Should().Be(DeviceUdn);
        entry.Context.ServiceId.Should().Be("urn:upnp-org:serviceId:RenderingControl");
    }

    [Fact]
    [Trait("ac", "AC-3.3.1")]
    public async Task InitializeAsync_ParseThrows_AllStayText_EmitsOneScpdParse()
    {
        var action = ActionWith("SetMode", InVar("DesiredMode", "Mode"));
        var vm = MakeInitVm(action, Table(), out _, out var diag,
            parseThrower: () => new UpnpProtocolException(new Uri("http://x/scpd"), "malformed state table"));

        await vm.InitializeAsync();

        vm.Inputs.Should().ContainSingle().Which.Should().BeOfType<ArgumentInputViewModel>();
        diag.Entries.Should().ContainSingle().Which.Category.Should().Be(DiagCategories.ScpdParse);
    }

    [Fact]
    [Trait("ac", "AC-3.3.1")]
    public async Task InitializeAsync_Cancelled_Swallowed_NoDiagnostic_InputsUnchanged()
    {
        var action = ActionWith("SetMode", InVar("DesiredMode", "Mode"));
        var table = Table(Sv("Mode", "string", allowedValueList: L("Stereo")));
        var vm = MakeInitVm(action, table, out _, out var diag,
            scpdResponder: (_, ct) => throw new OperationCanceledException(ct));

        await vm.InitializeAsync();

        vm.Inputs.Should().ContainSingle().Which.Should().BeOfType<ArgumentInputViewModel>("cancellation leaves the ctor inputs");
        diag.Entries.Should().BeEmpty("cancellation emits no diagnostic");
        vm.IsLoadingInputs.Should().BeFalse();
    }

    [Fact]
    [Trait("ac", "AC-3.3.1")]
    public async Task InitializeAsync_ArgumentLessAction_ReturnsImmediately_NotLoading()
    {
        var vm = MakeInitVm(Action("GetVolume"), Table(), out var http, out _);

        await vm.InitializeAsync();

        vm.IsLoadingInputs.Should().BeFalse("an argument-less action has nothing to load");
        vm.Inputs.Should().BeEmpty();
        http.RequestedUrls.Should().BeEmpty("no SCPD fetch when there are no inputs");
    }

    // ─── Marshalling regression (the Story 3.2 smoke crash class) ────────────────

    [Fact]
    [Trait("ac", "AC-3.3.1")]
    public async Task InitializeAsync_MarshalsRebuildThroughDispatcher_NotDirectly()
    {
        // Regression guard for winui-no-synccontext-marshal-vm: the post-await continuation runs on a
        // thread-pool thread; the Inputs rebuild + IsLoadingInputs clear MUST go through IUiDispatcher
        // or the bound window pokes UIElement off-thread → RPC_E_WRONGTHREAD → crash. A DeferredUiDispatcher
        // proves it: after await InitializeAsync() returns, the rebuild has NOT been applied until Drain().
        var ui = new DeferredUiDispatcher();
        var action = ActionWith("SetMode", InVar("DesiredMode", "Mode"));
        var table = Table(Sv("Mode", "string", "Mono", allowedValueList: L("Stereo", "Mono")));
        var vm = MakeInitVm(action, table, out _, out _, ui: ui);

        await vm.InitializeAsync();

        // Before drain: still the ctor's text-only input, still "loading" — proving the rebuild was Posted.
        vm.Inputs.Should().ContainSingle().Which.Should().BeOfType<ArgumentInputViewModel>(
            "the rebuilt Inputs must be marshalled through the UI dispatcher, not applied directly off-thread");
        vm.IsLoadingInputs.Should().BeTrue("IsLoadingInputs=false is part of the marshalled rebuild");
        ui.PostCount.Should().BeGreaterThan(0, "the VM must Post its Inputs rebuild");

        ui.Drain();

        vm.Inputs.Should().ContainSingle().Which.Should().BeOfType<AllowedValueListArgumentViewModel>();
        vm.IsLoadingInputs.Should().BeFalse();
    }

    // ─── Off-step Invoke gate (AC-3.3.6) ─────────────────────────────────────────

    [Fact]
    [Trait("ac", "AC-3.3.6")]
    public async Task Invoke_OffStepRangeInput_ShortCircuits_NoSoapCall_FR103()
    {
        var action = ActionWith("SetVolume", InVar("DesiredVolume", "Volume"));
        var table = Table(Sv("Volume", "ui4", "0", allowedValueRange: new ScpdAllowedValueRange(0, 10, 2)));
        var vm = MakeInitVm(action, table, out var http, out _);
        await vm.InitializeAsync();
        var range = (AllowedValueRangeArgumentViewModel)vm.Inputs[0];
        range.NumericValue = 3; // off-step

        await vm.InvokeCommand.ExecuteAsync(null);

        http.InvokedRequests.Should().BeEmpty("an off-step range input refuses to send (no SOAP request fires)");
        range.ValidationError.Should().NotBeNull();
        vm.IsInvoking.Should().BeFalse();
    }

    [Fact]
    [Trait("ac", "AC-3.3.6")]
    public async Task Invoke_OnStepRangeInput_Proceeds_SendsInvariantValue_FR103()
    {
        var action = ActionWith("SetVolume", InVar("DesiredVolume", "Volume"));
        var table = Table(Sv("Volume", "ui4", "0", allowedValueRange: new ScpdAllowedValueRange(0, 100, 1)));
        var vm = MakeInitVm(action, table, out var http, out _);
        http.InvokeResponder = (_, _) => Task.FromResult(new SoapResponse("SetVolume", Array.Empty<SoapArgument>()));
        await vm.InitializeAsync();
        ((AllowedValueRangeArgumentViewModel)vm.Inputs[0]).NumericValue = 42;

        await vm.InvokeCommand.ExecuteAsync(null);

        var req = http.InvokedRequests.Should().ContainSingle().Subject;
        req.InputArguments.Should().ContainSingle()
            .Which.Should().Be(new SoapArgument("DesiredVolume", "42"), "the invariant-formatted value flows uniformly through ResolvedValue");
    }

    [Fact]
    [Trait("ac", "AC-3.3.11")]
    public async Task Invoke_ListSelection_FlowsThroughResolvedValue_FR102()
    {
        var action = ActionWith("SetMode", InVar("DesiredMode", "Mode"));
        var table = Table(Sv("Mode", "string", "Stereo", allowedValueList: L("Stereo", "Mono", "Surround")));
        var vm = MakeInitVm(action, table, out var http, out _);
        http.InvokeResponder = (_, _) => Task.FromResult(new SoapResponse("SetMode", Array.Empty<SoapArgument>()));
        await vm.InitializeAsync();
        ((AllowedValueListArgumentViewModel)vm.Inputs[0]).SelectedValue = "Surround";

        await vm.InvokeCommand.ExecuteAsync(null);

        http.InvokedRequests.Should().ContainSingle()
            .Which.InputArguments.Should().ContainSingle()
            .Which.Should().Be(new SoapArgument("DesiredMode", "Surround"));
    }

    // ─── Integration: real parser over the rich fixture ──────────────────────────

    [Fact]
    [Trait("ac", "AC-3.3.1")]
    public async Task InitializeAsync_RealParser_OverRichFixture_ResolvesAllVariants()
    {
        // Drive the REAL XmlReaderScpdParser over state-table-rich.xml to prove the consumer-side
        // resolution wires to the actual parser output (not just hand-built tables).
        var fixture = System.IO.File.ReadAllBytes(
            System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", "Scpds", "state-table-rich.xml"));
        var action = ActionWith("SetAll",
            InVar("DesiredMute", "Mute"),       // boolean, no list/range → text
            InVar("DesiredVolume", "Volume"),   // ui4 range step 1 → range
            InVar("DesiredBalance", "Balance"), // i4 range no-step → range
            InVar("DesiredMode", "Mode"));      // string list → list
        var http = new StubUpnpHttpClient { ScpdResponder = (_, _) => Task.FromResult(fixture) };
        var diag = new CapturingDiagnosticEmitter();
        var registry = new FakeDeviceRegistry();
        var vm = new InvocationPopupViewModel(
            action, Service(), Entry(), http, new InlineUiDispatcher(), diag, registry,
            new ohSpy.Core.Scpd.XmlReaderScpdParser());

        await vm.InitializeAsync();

        vm.Inputs[0].Should().BeOfType<ArgumentInputViewModel>("boolean Mute has neither list nor range → text");
        var volume = vm.Inputs[1].Should().BeOfType<AllowedValueRangeArgumentViewModel>().Subject;
        volume.Maximum.Should().Be(100);
        volume.Step.Should().Be(1);
        volume.NumericValue.Should().Be(50);
        vm.Inputs[2].Should().BeOfType<AllowedValueRangeArgumentViewModel>().Which.Step.Should().BeNull();
        vm.Inputs[3].Should().BeOfType<AllowedValueListArgumentViewModel>().Which.AllowedValues
            .Should().Equal("Stereo", "Mono", "Surround");
        diag.Entries.Should().BeEmpty("the rich fixture is all well-formed");
    }
}
