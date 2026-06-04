namespace ohSpy.Core.Tests.ViewModels;

using FluentAssertions;
using ohSpy.Core.Devices;
using ohSpy.Core.Diagnostics;
using ohSpy.Core.Models;
using ohSpy.Core.Tests.Fakes;
using ohSpy.Core.ViewModels;

/// <summary>
/// Story 2.5 — <see cref="DeviceNodeViewModel"/> unit tests.
/// Covers AC-2.5.4, AC-2.5.5, AC-2.5.8 (plus Story 2.6 device-expand AC-2.6.2).
/// </summary>
public sealed class DeviceNodeViewModelTests
{
    private static readonly Uri BaseLocation = new("http://192.168.1.100:49152/desc.xml");

    // Inert NodeServices — the Story 2.5 tests never trigger an expand, so the stubs are
    // never invoked. The Story 2.6 device-expand tests below build the service list
    // synchronously (no SCPD fetch), so the HTTP/parser stubs stay untouched there too.
    private static readonly NodeServices NodeServices = new(
        new StubUpnpHttpClient(), new StubScpdParser(), new InlineUiDispatcher(),
        new CapturingDiagnosticEmitter(), new FakeUriLauncher(), new FakePropertiesLauncher(),
        new FakeInvocationPopupLauncher(), new FakeSubscriptionPopupLauncher());

    private static RegistryEntry PendingEntry(Guid? uuid = null, Uri? location = null) =>
        new(uuid ?? Guid.NewGuid(), location ?? BaseLocation, DateTime.UtcNow, CancellationToken.None);

    private static RegistryEntry LoadedEntry(
        Guid? uuid = null,
        Uri? location = null,
        string friendlyName = "Test Device",
        string deviceType = "urn:schemas-upnp-org:device:Basic:1")
    {
        var entry = PendingEntry(uuid, location);
        entry.MarkInFlight();
        entry.MarkLoaded(new DeviceDescription(
            friendlyName, deviceType, $"uuid:{entry.Uuid}",
            null, "Test Manufacturer", null, "Test Model",
            null, null, null, null, null,
            Array.Empty<ServiceDescription>()));
        return entry;
    }

    [Fact]
    [Trait("ac", "AC-2.5.4")]
    public void Constructor_WithLoadedEntry_SetsFriendlyName_AC254()
    {
        var entry = LoadedEntry(friendlyName: "Linn Klimax DSM");
        var vm = new DeviceNodeViewModel(entry, NodeServices);

        vm.FriendlyName.Should().Be("Linn Klimax DSM");
    }

    [Fact]
    [Trait("ac", "AC-2.5.4")]
    public void Constructor_WithNullFriendlyName_FallsBackToUuid_AC254()
    {
        var uuid = Guid.NewGuid();
        var entry = PendingEntry(uuid: uuid); // Description is null
        var vm = new DeviceNodeViewModel(entry, NodeServices);

        vm.FriendlyName.Should().Be($"uuid:{uuid}");
    }

    [Fact]
    [Trait("ac", "AC-A1.1")]
    public void Constructor_InitializesChildrenWithPlaceholder_ACA11()
    {
        var vm = new DeviceNodeViewModel(PendingEntry(), NodeServices);

        vm.Children.Count.Should().Be(1);
        vm.Children[0].Should().BeOfType<LoadingPlaceholderViewModel>();
    }

    [Fact]
    [Trait("ac", "AC-2.5.4")]
    public void Constructor_KindIsDevice_AC254()
    {
        var vm = new DeviceNodeViewModel(PendingEntry(), NodeServices);

        vm.Kind.Should().Be(NodeKind.Device);
    }

    [Fact]
    [Trait("ac", "AC-2.5.4")]
    public void SecondaryDetail_FormatsDeviceTypeTailAndHostPort_AC254()
    {
        var location = new Uri("http://192.168.1.100:49152/desc.xml");
        var entry = LoadedEntry(
            location: location,
            deviceType: "urn:schemas-upnp-org:device:MediaRenderer:1");
        var vm = new DeviceNodeViewModel(entry, NodeServices);

        vm.SecondaryDetail.Should().Be("MediaRenderer:1 · 192.168.1.100:49152");
    }

    [Fact]
    [Trait("ac", "AC-2.5.4")]
    public void SecondaryDetail_WhenDeviceTypeHasNoDeviceMarker_UsesFullType_AC254()
    {
        var entry = LoadedEntry(deviceType: "upnp:rootdevice");
        var vm = new DeviceNodeViewModel(entry, NodeServices);

        vm.SecondaryDetail.Should().Be("upnp:rootdevice · 192.168.1.100:49152");
    }

    [Fact]
    [Trait("ac", "AC-2.5.4")]
    public void SecondaryDetail_EmptyDeviceType_OmitsSeparator_AC254()
    {
        // Review patch P5: a degenerate empty type tail must not render an orphaned " · ".
        var entry = LoadedEntry(deviceType: "");
        var vm = new DeviceNodeViewModel(entry, NodeServices);

        vm.SecondaryDetail.Should().Be("192.168.1.100:49152");
    }

    [Fact]
    [Trait("ac", "AC-2.5.4")]
    public void SecondaryDetail_UnresolvablePort_OmitsPort_AC254()
    {
        // Review patch P5: a LocationUrl whose scheme has no default port (Uri.Port == -1)
        // must not render "host:-1".
        var location = new Uri("unknownscheme://192.168.1.100/desc.xml");
        location.Port.Should().Be(-1, "guard precondition: this scheme has no default port");
        var entry = LoadedEntry(location: location, deviceType: "urn:schemas-upnp-org:device:Basic:1");
        var vm = new DeviceNodeViewModel(entry, NodeServices);

        vm.SecondaryDetail.Should().Be("Basic:1 · 192.168.1.100");
    }

    [Fact]
    [Trait("ac", "AC-2.5.8")]
    public void RefreshFrom_UpdatesFriendlyNameAndSecondaryDetail_AC258()
    {
        var uuid = Guid.NewGuid();
        var entry = LoadedEntry(uuid: uuid, friendlyName: "Old Name");
        var vm = new DeviceNodeViewModel(entry, NodeServices);

        var newEntry = LoadedEntry(uuid: uuid, friendlyName: "New Name",
            deviceType: "urn:schemas-upnp-org:device:MediaServer:1");
        vm.RefreshFrom(newEntry);

        vm.FriendlyName.Should().Be("New Name");
        vm.SecondaryDetail.Should().Be("MediaServer:1 · 192.168.1.100:49152");
    }

    [Fact]
    [Trait("ac", "AC-A1.4")]
    public void ReplaceWith_ReplacesChildrenCollection_ACA14()
    {
        var vm = new DeviceNodeViewModel(PendingEntry(), NodeServices);

        vm.ReplaceWith([new InlineErrorViewModel("err")]);

        vm.Children.Count.Should().Be(1);
        vm.Children[0].Should().BeOfType<InlineErrorViewModel>();
    }

    [Fact]
    [Trait("ac", "AC-2.5.5")]
    public void LoadingPlaceholderViewModel_LabelAndKind_AC255()
    {
        var placeholder = new LoadingPlaceholderViewModel();

        placeholder.Label.Should().Be("Loading…");
        placeholder.Kind.Should().Be(NodeKind.Placeholder);
    }

    [Fact]
    [Trait("ac", "AC-2.5.5")]
    public void InlineErrorViewModel_LabelAndKind_AC255()
    {
        var error = new InlineErrorViewModel("failed");

        error.Label.Should().Be("failed");
        error.Kind.Should().Be(NodeKind.Error);
    }

    // ─── Story 2.6 — device expand → service list (AC-2.6.2) ─────────────────────

    private static ServiceDescription Svc(string serviceType, string scpdUrl) =>
        new(serviceType, $"urn:upnp-org:serviceId:{serviceType}", scpdUrl, "/ctrl", "/evt");

    private static RegistryEntry LoadedEntryWithServices(params ServiceDescription[] services)
    {
        var entry = PendingEntry();
        entry.MarkInFlight();
        entry.MarkLoaded(new DeviceDescription(
            "Test Device", "urn:schemas-upnp-org:device:MediaRenderer:1", $"uuid:{entry.Uuid}",
            null, "Mfr", null, "Model",
            null, null, null, null, null,
            services));
        return entry;
    }

    [Fact]
    [Trait("ac", "AC-2.6.2")]
    public void Expand_ReplacesPlaceholderWithServiceNodes_AC262()
    {
        var entry = LoadedEntryWithServices(
            Svc("urn:schemas-upnp-org:service:RenderingControl:1", "/RC/Scpd.xml"),
            Svc("urn:schemas-upnp-org:service:ConnectionManager:1", "/CM/Scpd.xml"));
        var vm = new DeviceNodeViewModel(entry, NodeServices);

        vm.IsExpanded = true;

        vm.Children.Should().HaveCount(2);
        vm.Children.Should().AllBeOfType<ServiceNodeViewModel>();
        vm.Children.Select(c => c.Label).Should().Equal("RenderingControl:1", "ConnectionManager:1");
        vm.Children.OfType<LoadingPlaceholderViewModel>().Should().BeEmpty();
    }

    [Fact]
    [Trait("ac", "AC-2.6.2")]
    public void Expand_NoHttpFetchTriggered_AC262()
    {
        // ScpdResponder left null — it throws if the SCPD path is hit. Building the service
        // list is synchronous and fetch-free, so expanding the DEVICE must not touch HTTP.
        var http = new StubUpnpHttpClient();
        var services = new NodeServices(http, new StubScpdParser(), new InlineUiDispatcher(),
            new CapturingDiagnosticEmitter(), new FakeUriLauncher(), new FakePropertiesLauncher(),
            new FakeInvocationPopupLauncher(), new FakeSubscriptionPopupLauncher());
        var entry = LoadedEntryWithServices(
            Svc("urn:schemas-upnp-org:service:RenderingControl:1", "/RC/Scpd.xml"));
        var vm = new DeviceNodeViewModel(entry, services);

        var act = () => vm.IsExpanded = true;

        act.Should().NotThrow();
        http.RequestedUrls.Should().BeEmpty("expanding a device builds its service list without fetching");
    }

    [Fact]
    [Trait("ac", "AC-2.6.2")]
    public void Expand_Twice_DoesNotRebuildServiceList_AC262()
    {
        var entry = LoadedEntryWithServices(
            Svc("urn:schemas-upnp-org:service:RenderingControl:1", "/RC/Scpd.xml"),
            Svc("urn:schemas-upnp-org:service:ConnectionManager:1", "/CM/Scpd.xml"));
        var vm = new DeviceNodeViewModel(entry, NodeServices);

        vm.IsExpanded = true;
        var firstBuild = vm.Children.ToArray();

        vm.IsExpanded = false;
        vm.IsExpanded = true;

        vm.Children.Should().Equal(firstBuild,
            "the once-guard keeps the same ServiceNodeViewModel instances (no second Reset)");
    }

    [Fact]
    [Trait("ac", "AC-2.6.2")]
    public void Expand_EmptyServiceList_ClearsPlaceholder_AC262()
    {
        var entry = LoadedEntryWithServices(); // no services
        var vm = new DeviceNodeViewModel(entry, NodeServices);

        vm.IsExpanded = true;

        vm.Children.Should().BeEmpty();
    }

    // ─── Story 2.8: context-menu commands (AC-2.8.1/2.8.2/2.8.3) ────────────────

    private static (NodeServices services, FakeUriLauncher launcher, CapturingDiagnosticEmitter diag,
        FakePropertiesLauncher properties) CapturingServices()
    {
        var launcher = new FakeUriLauncher();
        var diag = new CapturingDiagnosticEmitter();
        var properties = new FakePropertiesLauncher();
        return (new NodeServices(new StubUpnpHttpClient(), new StubScpdParser(),
            new InlineUiDispatcher(), diag, launcher, properties, new FakeInvocationPopupLauncher(),
            new FakeSubscriptionPopupLauncher()),
            launcher, diag, properties);
    }

    [Fact]
    [Trait("ac", "AC-2.8.2")]
    public void FetchXmlCommand_OpensLocationUrl_AC282()
    {
        var (services, launcher, diag, _) = CapturingServices();
        var location = new Uri("http://192.168.1.100:49152/desc.xml");
        var vm = new DeviceNodeViewModel(LoadedEntry(location: location), services);

        vm.FetchXmlCommand.Execute(null);

        launcher.Launched.Should().ContainSingle().Which.Should().Be(location);
        diag.Entries.Should().BeEmpty();
    }

    [Fact]
    [Trait("ac", "AC-2.8.3")]
    public void FetchXmlCommand_NonHttpLocation_Refused_Warns_AC283()
    {
        var (services, launcher, diag, _) = CapturingServices();
        var vm = new DeviceNodeViewModel(LoadedEntry(location: new Uri("file:///x/desc.xml")), services);

        vm.FetchXmlCommand.Execute(null);

        launcher.Launched.Should().BeEmpty();
        diag.Entries.Should().ContainSingle()
            .Which.Category.Should().Be(DiagCategories.ShellExecute);
    }

    [Fact]
    [Trait("ac", "AC-2.8.2")]
    public void FetchXmlCommand_LaunchFailure_Warns_NoCrash_AC282()
    {
        var (services, launcher, diag, _) = CapturingServices();
        launcher.ThrowOnLaunch = new InvalidOperationException("no browser");
        var uuid = Guid.NewGuid();
        var vm = new DeviceNodeViewModel(LoadedEntry(uuid: uuid), services);

        var act = () => vm.FetchXmlCommand.Execute(null);

        act.Should().NotThrow();
        var warning = diag.Entries.Should().ContainSingle().Which;
        warning.Category.Should().Be(DiagCategories.ShellExecute);
        warning.Context.DeviceUuid.Should().Be(uuid);
    }

    [Fact]
    [Trait("ac", "AC-2.9.7")]
    public void OpenPropertiesCommand_OpensPropertiesWindow_AC297()
    {
        // Story 2.9 replaces the 2.8 "not yet implemented" stub: the command now crosses the
        // Core/App seam via IPropertiesLauncher, handing the device's RegistryEntry across.
        var (services, launcher, diag, properties) = CapturingServices();
        var uuid = Guid.NewGuid();
        var vm = new DeviceNodeViewModel(LoadedEntry(uuid: uuid), services);

        var act = () => vm.OpenPropertiesCommand.Execute(null);

        act.Should().NotThrow();
        launcher.Launched.Should().BeEmpty("opening Properties is not a shell-open");
        diag.Entries.Should().BeEmpty("the 2.8 NotImplemented warning is removed in 2.9");
        properties.Opened.Should().ContainSingle().Which.Uuid.Should().Be(uuid);
    }
}
