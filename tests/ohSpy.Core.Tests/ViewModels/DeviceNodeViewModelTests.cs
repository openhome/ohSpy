namespace ohSpy.Core.Tests.ViewModels;

using FluentAssertions;
using ohSpy.Core.Devices;
using ohSpy.Core.Models;
using ohSpy.Core.ViewModels;

/// <summary>
/// Story 2.5 — <see cref="DeviceNodeViewModel"/> unit tests.
/// Covers AC-2.5.4, AC-2.5.5, AC-2.5.8.
/// </summary>
public sealed class DeviceNodeViewModelTests
{
    private static readonly Uri BaseLocation = new("http://192.168.1.100:49152/desc.xml");

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
        var vm = new DeviceNodeViewModel(entry);

        vm.FriendlyName.Should().Be("Linn Klimax DSM");
    }

    [Fact]
    [Trait("ac", "AC-2.5.4")]
    public void Constructor_WithNullFriendlyName_FallsBackToUuid_AC254()
    {
        var uuid = Guid.NewGuid();
        var entry = PendingEntry(uuid: uuid); // Description is null
        var vm = new DeviceNodeViewModel(entry);

        vm.FriendlyName.Should().Be($"uuid:{uuid}");
    }

    [Fact]
    [Trait("ac", "AC-A1.1")]
    public void Constructor_InitializesChildrenWithPlaceholder_ACA11()
    {
        var vm = new DeviceNodeViewModel(PendingEntry());

        vm.Children.Count.Should().Be(1);
        vm.Children[0].Should().BeOfType<LoadingPlaceholderViewModel>();
    }

    [Fact]
    [Trait("ac", "AC-2.5.4")]
    public void Constructor_KindIsDevice_AC254()
    {
        var vm = new DeviceNodeViewModel(PendingEntry());

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
        var vm = new DeviceNodeViewModel(entry);

        vm.SecondaryDetail.Should().Be("MediaRenderer:1 · 192.168.1.100:49152");
    }

    [Fact]
    [Trait("ac", "AC-2.5.4")]
    public void SecondaryDetail_WhenDeviceTypeHasNoDeviceMarker_UsesFullType_AC254()
    {
        var entry = LoadedEntry(deviceType: "upnp:rootdevice");
        var vm = new DeviceNodeViewModel(entry);

        vm.SecondaryDetail.Should().Be("upnp:rootdevice · 192.168.1.100:49152");
    }

    [Fact]
    [Trait("ac", "AC-2.5.4")]
    public void SecondaryDetail_EmptyDeviceType_OmitsSeparator_AC254()
    {
        // Review patch P5: a degenerate empty type tail must not render an orphaned " · ".
        var entry = LoadedEntry(deviceType: "");
        var vm = new DeviceNodeViewModel(entry);

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
        var vm = new DeviceNodeViewModel(entry);

        vm.SecondaryDetail.Should().Be("Basic:1 · 192.168.1.100");
    }

    [Fact]
    [Trait("ac", "AC-2.5.8")]
    public void RefreshFrom_UpdatesFriendlyNameAndSecondaryDetail_AC258()
    {
        var uuid = Guid.NewGuid();
        var entry = LoadedEntry(uuid: uuid, friendlyName: "Old Name");
        var vm = new DeviceNodeViewModel(entry);

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
        var vm = new DeviceNodeViewModel(PendingEntry());

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
}
