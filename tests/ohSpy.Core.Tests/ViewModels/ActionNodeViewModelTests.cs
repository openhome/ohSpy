namespace ohSpy.Core.Tests.ViewModels;

using FluentAssertions;
using ohSpy.Core.Devices;
using ohSpy.Core.Models;
using ohSpy.Core.Tests.Fakes;
using ohSpy.Core.ViewModels;

/// <summary>
/// Story 2.6 — <see cref="ActionNodeViewModel"/> unit tests (AC-2.6.7 leaf shape) +
/// Story 3.2 (AC-3.2.4) — OpenInvocationPopupCommand crosses the Core/App seam.
/// </summary>
public sealed class ActionNodeViewModelTests
{
    private static readonly Uri DeviceLocation = new("http://192.168.1.100:49152/desc.xml");

    private static ScpdAction Action(string name) =>
        new(name, Array.Empty<ScpdArgument>(), Array.Empty<ScpdArgument>());

    private static ServiceDescription Service() =>
        new("urn:schemas-upnp-org:service:RenderingControl:1",
            "urn:upnp-org:serviceId:RenderingControl", "/RC/Scpd.xml", "/RC/ctrl", "/RC/evt");

    private static RegistryEntry Entry() =>
        new(Guid.NewGuid(), DeviceLocation, DateTime.UtcNow, CancellationToken.None);

    private static NodeServices Services(IInvocationPopupLauncher? popup = null) =>
        new(new StubUpnpHttpClient(), new StubScpdParser(), new InlineUiDispatcher(),
            new CapturingDiagnosticEmitter(), new FakeUriLauncher(), new FakePropertiesLauncher(),
            popup ?? new FakeInvocationPopupLauncher());

    private static ActionNodeViewModel Node(string name, NodeServices? services = null) =>
        new(Action(name), Service(), Entry(), services ?? Services());

    [Fact]
    [Trait("ac", "AC-2.6.7")]
    public void Constructor_LabelIsActionName_AC267()
    {
        var vm = Node("Play");

        vm.Label.Should().Be("Play");
    }

    [Fact]
    [Trait("ac", "AC-2.6.7")]
    public void Kind_IsAction_AC267()
    {
        var vm = Node("Play");

        vm.Kind.Should().Be(NodeKind.Action);
    }

    [Fact]
    [Trait("ac", "AC-A1.3")]
    public void Children_IsEmpty_ACA13()
    {
        var vm = Node("Play");

        vm.Children.Count.Should().Be(0); // leaf; no placeholder → XAML renders no chevron
    }

    [Fact]
    [Trait("ac", "AC-3.2.4")]
    public void OpenInvocationPopupCommand_OpensPopupWithActionServiceEntry_AC324()
    {
        var popup = new FakeInvocationPopupLauncher();
        var action = Action("SetVolume");
        var service = Service();
        var entry = Entry();
        var vm = new ActionNodeViewModel(action, service, entry, Services(popup));

        var act = () => vm.OpenInvocationPopupCommand.Execute(null);

        act.Should().NotThrow();
        var opened = popup.Opened.Should().ContainSingle().Subject;
        opened.Action.Should().BeSameAs(action);
        opened.Service.Should().BeSameAs(service);
        opened.Entry.Should().BeSameAs(entry);
    }
}
