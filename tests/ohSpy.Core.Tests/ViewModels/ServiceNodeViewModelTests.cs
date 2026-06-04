namespace ohSpy.Core.Tests.ViewModels;

using System.Diagnostics;
using FluentAssertions;
using ohSpy.Core.Devices;
using ohSpy.Core.Diagnostics;
using ohSpy.Core.Http;
using ohSpy.Core.Models;
using ohSpy.Core.Scpd;
using ohSpy.Core.Shell;
using ohSpy.Core.Tests.Fakes;
using ohSpy.Core.Threading;
using ohSpy.Core.ViewModels;

/// <summary>
/// Story 2.6 — <see cref="ServiceNodeViewModel"/> unit tests. Uses <see cref="InlineUiDispatcher"/>
/// so <c>Post</c> runs inline; the error/cancellation paths complete synchronously, while the
/// happy-path action stream hops threads through the parser's <c>Task.Yield</c> and is awaited
/// to quiescence via <see cref="WaitUntilAsync"/>.
/// </summary>
public sealed class ServiceNodeViewModelTests
{
    private static readonly Uri DeviceLocation = new("http://192.168.1.100:49152/desc.xml");
    private const string DeviceUdn = "uuid:11111111-1111-1111-1111-111111111111";

    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Scpds", name);

    private static ServiceDescription Service(
        string serviceType = "urn:schemas-upnp-org:service:RenderingControl:1",
        string serviceId = "urn:upnp-org:serviceId:RenderingControl",
        string scpdUrl = "/RC/Scpd.xml") =>
        new(serviceType, serviceId, scpdUrl, "/RC/ctrl", "/RC/evt");

    private static ScpdAction Action(string name) =>
        new(name, Array.Empty<ScpdArgument>(), Array.Empty<ScpdArgument>());

    private static NodeServices MakeNodeServices(
        StubUpnpHttpClient http, IScpdParser parser,
        IUiDispatcher? ui = null, IDiagnosticEmitter? diag = null, IUriLauncher? launcher = null,
        IPropertiesLauncher? propertiesLauncher = null,
        IInvocationPopupLauncher? invocationPopupLauncher = null,
        ISubscriptionPopupLauncher? subscriptionPopupLauncher = null) =>
        new(http, parser, ui ?? new InlineUiDispatcher(), diag ?? new CapturingDiagnosticEmitter(),
            launcher ?? new FakeUriLauncher(), propertiesLauncher ?? new FakePropertiesLauncher(),
            invocationPopupLauncher ?? new FakeInvocationPopupLauncher(),
            subscriptionPopupLauncher ?? new FakeSubscriptionPopupLauncher());

    // Story 3.2: ServiceNodeViewModel now takes the device RegistryEntry (threaded to ActionNodes).
    private static RegistryEntry Entry(Uri? location = null) =>
        new(DeviceUdn, location ?? DeviceLocation, DateTime.UtcNow, CancellationToken.None);

    private static ServiceNodeViewModel NewVm(
        NodeServices services, ServiceDescription? service = null,
        Uri? location = null, CancellationToken deviceToken = default) =>
        new(service ?? Service(), location ?? DeviceLocation, DeviceUdn,
            Entry(location), services, deviceToken);

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition() && sw.ElapsedMilliseconds < timeoutMs)
            await Task.Delay(5);
        condition().Should().BeTrue($"the expected state was not reached within {timeoutMs}ms");
    }

    // ─── Construction / shape (AC-2.6.1) ───────────────────────────────────────

    [Fact]
    [Trait("ac", "AC-2.6.1")]
    public void Constructor_InitializesPlaceholderChild_ACA12()
    {
        var vm = NewVm(MakeNodeServices(new StubUpnpHttpClient(), new StubScpdParser()));

        vm.Children.Count.Should().Be(1);
        vm.Children[0].Should().BeOfType<LoadingPlaceholderViewModel>();
    }

    [Fact]
    [Trait("ac", "AC-2.6.1")]
    public void Constructor_KindIsService_AC261()
    {
        var vm = NewVm(MakeNodeServices(new StubUpnpHttpClient(), new StubScpdParser()));

        vm.Kind.Should().Be(NodeKind.Service);
    }

    [Fact]
    [Trait("ac", "AC-2.6.1")]
    public void Label_FromServiceTypeTail_AC261()
    {
        var services = MakeNodeServices(new StubUpnpHttpClient(), new StubScpdParser());

        var fromType = NewVm(services,
            Service(serviceType: "urn:schemas-upnp-org:service:MediaRenderer:1"));
        fromType.Label.Should().Be("MediaRenderer:1");

        // Empty serviceType → falls back to serviceId per ComputeLabel.
        var fromId = NewVm(services,
            Service(serviceType: "", serviceId: "urn:upnp-org:serviceId:Foo"));
        fromId.Label.Should().Be("urn:upnp-org:serviceId:Foo");
    }

    // ─── Happy-path incremental stream (AC-2.6.3) ───────────────────────────────

    [Fact]
    [Trait("ac", "AC-2.6.3")]
    public async Task FirstExpand_HappyPath_StreamsActionsInOrder_RemovesPlaceholder_AC263()
    {
        var http = new StubUpnpHttpClient
        {
            ScpdResponder = (_, _) => Task.FromResult(new byte[] { 1, 2, 3 }),
        };
        var parser = new StubScpdParser
        {
            Actions = new[] { Action("GetMute"), Action("SetMute"), Action("GetVolume") },
        };
        var vm = NewVm(MakeNodeServices(http, parser));

        vm.IsExpanded = true;
        await WaitUntilAsync(() => vm.Children.Count == 3);

        vm.Children.Should().AllBeOfType<ActionNodeViewModel>();
        vm.Children.Select(c => c.Label).Should().Equal("GetMute", "SetMute", "GetVolume");
        vm.Children.OfType<LoadingPlaceholderViewModel>().Should().BeEmpty();
    }

    [Fact]
    [Trait("ac", "AC-2.6.3")]
    public async Task FirstExpand_RealParser_LinnDs5Action_AC263()
    {
        var bytes = await File.ReadAllBytesAsync(FixturePath("linn-ds-5action.xml"));
        var http = new StubUpnpHttpClient { ScpdResponder = (_, _) => Task.FromResult(bytes) };
        var vm = NewVm(MakeNodeServices(http, new XmlReaderScpdParser()));

        vm.IsExpanded = true;
        await WaitUntilAsync(() => vm.Children.Count == 5);

        vm.Children.Select(c => c.Label)
            .Should().Equal("GetMute", "SetMute", "GetVolume", "SetVolume", "VolumeInc");
    }

    // ─── Failure paths (AC-2.6.4) ───────────────────────────────────────────────

    [Fact]
    [Trait("ac", "AC-2.6.4")]
    public async Task Expand_FetchThrowsTimeout_ShowsInlineError_EmitsScpdFetchWarning_AC264()
    {
        var url = new Uri(DeviceLocation, "/RC/Scpd.xml");
        var http = new StubUpnpHttpClient
        {
            ScpdResponder = (_, _) =>
                throw new UpnpTimeoutException(url, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(11)),
        };
        var diag = new CapturingDiagnosticEmitter();
        var vm = NewVm(MakeNodeServices(http, new StubScpdParser(), diag: diag));

        vm.IsExpanded = true;
        await WaitUntilAsync(() => vm.Children.OfType<InlineErrorViewModel>().Any());

        vm.Children.Should().ContainSingle().Which.Should().BeOfType<InlineErrorViewModel>();
        var entry = diag.Entries.Should().ContainSingle().Subject;
        entry.Severity.Should().Be("Warning");
        entry.Category.Should().Be(DiagCategories.ScpdFetch);
        entry.Context.DeviceUuid.Should().Be(DeviceUdn);
        entry.Context.Url.Should().Be(url.ToString());
    }

    [Fact]
    [Trait("ac", "AC-2.6.4")]
    public async Task Expand_ParserThrowsProtocol_ShowsInlineError_EmitsScpdParseWarning_AC264()
    {
        var url = new Uri(DeviceLocation, "/RC/Scpd.xml");
        var http = new StubUpnpHttpClient { ScpdResponder = (_, _) => Task.FromResult(new byte[] { 1 }) };
        var parser = new StubScpdParser { Thrower = () => new UpnpProtocolException(url, "bad xml") };
        var diag = new CapturingDiagnosticEmitter();
        var vm = NewVm(MakeNodeServices(http, parser, diag: diag));

        vm.IsExpanded = true;
        await WaitUntilAsync(() => vm.Children.OfType<InlineErrorViewModel>().Any());

        vm.Children.Should().ContainSingle().Which.Should().BeOfType<InlineErrorViewModel>()
            .Which.Label.Should().Be("bad xml");
        var entry = diag.Entries.Should().ContainSingle().Subject;
        entry.Severity.Should().Be("Warning");
        entry.Category.Should().Be(DiagCategories.ScpdParse);
        entry.Context.ErrorText.Should().Be("bad xml");
    }

    [Fact]
    [Trait("ac", "AC-2.6.4")]
    public async Task Expand_FetchThrowsProtocol_Oversize_ShowsInlineError_EmitsScpdFetchWarning_AC264()
    {
        // Review F1: an oversize SCPD body surfaces as UpnpProtocolException FROM THE FETCH.
        // That is a fetch-layer failure → ScpdFetch, NOT ScpdParse (which is parser-layer only).
        var url = new Uri(DeviceLocation, "/RC/Scpd.xml");
        var http = new StubUpnpHttpClient
        {
            ScpdResponder = (_, _) => throw new UpnpProtocolException(url, "SCPD body exceeds size cap"),
        };
        var diag = new CapturingDiagnosticEmitter();
        var vm = NewVm(MakeNodeServices(http, new StubScpdParser(), diag: diag));

        vm.IsExpanded = true;
        await WaitUntilAsync(() => vm.Children.OfType<InlineErrorViewModel>().Any());

        vm.Children.Should().ContainSingle().Which.Should().BeOfType<InlineErrorViewModel>();
        var entry = diag.Entries.Should().ContainSingle().Subject;
        entry.Severity.Should().Be("Warning");
        entry.Category.Should().Be(DiagCategories.ScpdFetch,
            "an oversize body thrown by FetchScpdAsync is a fetch-layer failure");
        entry.Context.DeviceUuid.Should().Be(DeviceUdn);
    }

    // ─── No re-fetch on collapse/re-expand (AC-2.6.6) ───────────────────────────

    [Fact]
    [Trait("ac", "AC-2.6.6")]
    public async Task Expand_Twice_DoesNotRefetch_AC266()
    {
        var http = new StubUpnpHttpClient { ScpdResponder = (_, _) => Task.FromResult(new byte[] { 1 }) };
        var parser = new StubScpdParser { Actions = new[] { Action("Play"), Action("Stop") } };
        var vm = NewVm(MakeNodeServices(http, parser));

        vm.IsExpanded = true;
        await WaitUntilAsync(() => vm.Children.Count == 2);
        var afterFirst = vm.Children.ToArray();

        vm.IsExpanded = false;
        vm.IsExpanded = true;
        await Task.Delay(20); // give any (erroneous) re-fetch a chance to run

        http.RequestedUrls.Count(u => u == new Uri(DeviceLocation, "/RC/Scpd.xml"))
            .Should().Be(1, "a re-expand must not trigger a second SCPD fetch");
        vm.Children.Should().Equal(afterFirst, "the loaded action list is retained across collapse");
    }

    // ─── Cancellation (AC-2.6.8) ────────────────────────────────────────────────

    [Fact]
    [Trait("ac", "AC-2.6.8")]
    public async Task Expand_DeviceTokenCancelled_NoError_NoDiagnostic_AC268()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var http = new StubUpnpHttpClient { ScpdResponder = (_, _) => Task.FromResult(new byte[] { 1 }) };
        var parser = new StubScpdParser { Actions = new[] { Action("Play") } }; // observes ct mid-stream
        var diag = new CapturingDiagnosticEmitter();
        var vm = NewVm(MakeNodeServices(http, parser, diag: diag), deviceToken: cts.Token);

        vm.IsExpanded = true;
        await Task.Delay(20); // cancellation path completes synchronously inline; settle anyway

        vm.Children.OfType<InlineErrorViewModel>().Should().BeEmpty("cancellation is not a fault");
        diag.Entries.Should().BeEmpty("cancellation emits no diagnostic (AC-2.6.8)");
    }

    // ─── SCPD URL resolution (AC-2.6.3) ─────────────────────────────────────────

    [Fact]
    [Trait("ac", "AC-2.6.3")]
    public async Task ScpdUrl_RelativeResolvedAgainstLocation_AC263()
    {
        Uri? captured = null;
        var http = new StubUpnpHttpClient
        {
            ScpdResponder = (u, _) => { captured = u; return Task.FromResult(Array.Empty<byte>()); },
        };
        var vm = NewVm(
            MakeNodeServices(http, new StubScpdParser()),
            service: Service(scpdUrl: "/Foo/Scpd.xml"),
            location: new Uri("http://host:49152/desc.xml"));

        vm.IsExpanded = true;
        await WaitUntilAsync(() => captured is not null);

        captured.Should().Be(new Uri("http://host:49152/Foo/Scpd.xml"));
    }

    // ─── Story 2.8: context-menu commands (AC-2.8.4/2.8.5) ──────────────────────

    [Fact]
    [Trait("ac", "AC-2.8.4")]
    public void FetchServiceXmlCommand_ResolvesRelativeScpdUrl_Launches_AC284()
    {
        var launcher = new FakeUriLauncher();
        var services = MakeNodeServices(new StubUpnpHttpClient(), new StubScpdParser(), launcher: launcher);
        var vm = NewVm(services,
            service: Service(scpdUrl: "/RC/Scpd.xml"),
            location: new Uri("http://192.168.1.100:49152/desc.xml"));

        vm.FetchServiceXmlCommand.Execute(null);

        launcher.Launched.Should().ContainSingle()
            .Which.Should().Be(new Uri("http://192.168.1.100:49152/RC/Scpd.xml"));
    }

    [Fact]
    [Trait("ac", "AC-2.8.4")]
    public void FetchServiceXmlCommand_AbsoluteScpdUrl_PassesThrough_AC284()
    {
        var launcher = new FakeUriLauncher();
        var services = MakeNodeServices(new StubUpnpHttpClient(), new StubScpdParser(), launcher: launcher);
        var vm = NewVm(services,
            service: Service(scpdUrl: "http://10.0.0.5/scpd.xml"),
            location: new Uri("http://192.168.1.100:49152/desc.xml"));

        vm.FetchServiceXmlCommand.Execute(null);

        launcher.Launched.Should().ContainSingle()
            .Which.Should().Be(new Uri("http://10.0.0.5/scpd.xml"));
    }

    // AC-4.3.8: the 2.8 Feature.NotImplemented stub is replaced by the real launch — SubscribeCommand
    // now opens the subscription popup with this node's (service, entry), and emits NO diagnostic.
    [Fact]
    [Trait("ac", "AC-4.3.8")]
    public void SubscribeCommand_OpensSubscriptionPopup_WithServiceAndEntry_AC438()
    {
        var subLauncher = new FakeSubscriptionPopupLauncher();
        var diag = new CapturingDiagnosticEmitter();
        var service = Service(serviceType: "urn:schemas-upnp-org:service:AVTransport:1");
        var services = MakeNodeServices(new StubUpnpHttpClient(), new StubScpdParser(),
            diag: diag, subscriptionPopupLauncher: subLauncher);
        var vm = NewVm(services, service: service);

        var act = () => vm.SubscribeCommand.Execute(null);

        act.Should().NotThrow();
        var opened = subLauncher.Opened.Should().ContainSingle().Which;
        opened.Service.Should().BeSameAs(service);
        opened.Entry.Udn.Should().Be(DeviceUdn);
        diag.Entries.Should().BeEmpty("the real launch emits no diagnostic (the stub Warning is gone)");
    }
}
