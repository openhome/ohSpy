namespace ohSpy.Core.Tests.ViewModels;

using FluentAssertions;
using ohSpy.Core.Devices;
using ohSpy.Core.Diagnostics;
using ohSpy.Core.Models;
using ohSpy.Core.Tests.Fakes;
using ohSpy.Core.ViewModels;

/// <summary>
/// Story 2.9 — <see cref="PropertiesViewModel"/> unit tests (the automated-test heart of the
/// story; the windowing/XAML layer is App-only and manual-verify per Decision 4). Covers the
/// FR-052 field mapping, the absent→"—" rule, the FR-037 device-gone banner, the Story 2.8
/// hyperlink whitelist wiring, relative-URL resolution, and dispose/unsubscribe.
/// </summary>
public sealed class PropertiesViewModelTests
{
    private static readonly Uri Location = new("http://192.168.1.100:49152/desc.xml");

    private static RegistryEntry BuildEntry(
        string? registryUdn = null,
        Uri? location = null,
        DeviceDescription? description = null,
        string? server = "Linux/3.0 UPnP/1.0 ohSpy/1.0",
        TimeSpan? maxAge = null,
        string? bootId = "42",
        string? configId = "7",
        int aliveCount = 1)
    {
        var entry = new RegistryEntry(
            registryUdn ?? $"uuid:{Guid.NewGuid()}", location ?? Location, DateTime.UtcNow, CancellationToken.None);
        for (var i = 0; i < aliveCount; i++)
            entry.RefreshSsdpMetadata(DateTime.UtcNow, server, maxAge ?? TimeSpan.FromSeconds(1800), bootId, configId);
        if (description is not null)
        {
            entry.MarkInFlight();
            entry.MarkLoaded(description);
        }
        return entry;
    }

    private static DeviceDescription Description(
        string friendlyName = "Linn Klimax DSM",
        string deviceType = "urn:schemas-upnp-org:device:MediaRenderer:1",
        string? udn = null,
        string? presentationUrl = "http://192.168.1.100:49152/index.html",
        string manufacturer = "Linn",
        string? manufacturerUrl = "http://www.linn.co.uk",
        string modelName = "Klimax DSM",
        string? modelNumber = "Mk IV",
        string? modelDescription = "Network Music Player",
        string? modelUrl = "http://www.linn.co.uk/klimax",
        string? serialNumber = "SN-001",
        string? upc = "012345678905") =>
        new(friendlyName, deviceType, udn ?? "uuid:test-udn", presentationUrl,
            manufacturer, manufacturerUrl, modelName, modelNumber, modelDescription, modelUrl,
            serialNumber, upc, Array.Empty<ServiceDescription>());

    private static PropertiesViewModel NewVm(
        RegistryEntry entry, IDeviceRegistry? registry = null,
        FakeUriLauncher? launcher = null, CapturingDiagnosticEmitter? diag = null) =>
        new(entry, registry ?? new FakeDeviceRegistry(),
            launcher ?? new FakeUriLauncher(), diag ?? new CapturingDiagnosticEmitter());

    // ── Identity (AC-2.9.4) ──

    [Fact]
    [Trait("ac", "AC-2.9.4")]
    public void Identity_MapsFromDescription_AC294()
    {
        var udn = $"uuid:{Guid.NewGuid()}";
        var entry = BuildEntry(registryUdn: udn, description: Description(
            friendlyName: "Linn Klimax DSM",
            deviceType: "urn:schemas-upnp-org:device:MediaRenderer:1",
            udn: "uuid:abc",
            presentationUrl: "http://192.168.1.100:49152/index.html"));
        var vm = NewVm(entry);

        vm.FriendlyName.Should().Be("Linn Klimax DSM");
        vm.DeviceTypeUrn.Should().Be("urn:schemas-upnp-org:device:MediaRenderer:1");
        vm.Udn.Should().Be("uuid:abc");
        vm.PresentationUrl.Should().Be("http://192.168.1.100:49152/index.html");
        vm.Uuid.Should().Be(udn);
    }

    // ── Manufacturer (AC-2.9.4) ──

    [Fact]
    [Trait("ac", "AC-2.9.4")]
    public void Manufacturer_MapsAllEightFields_AC294()
    {
        var entry = BuildEntry(description: Description(
            manufacturer: "Linn", manufacturerUrl: "http://www.linn.co.uk",
            modelName: "Klimax DSM", modelNumber: null, modelDescription: "Music Player",
            modelUrl: "http://www.linn.co.uk/klimax", serialNumber: "SN-001", upc: null));
        var vm = NewVm(entry);

        vm.Manufacturer.Should().Be("Linn");
        vm.ManufacturerUrl.Should().Be("http://www.linn.co.uk");
        vm.ModelName.Should().Be("Klimax DSM");
        vm.ModelNumber.Should().Be("—", "absent ModelNumber renders as the muted placeholder");
        vm.ModelDescription.Should().Be("Music Player");
        vm.ModelUrl.Should().Be("http://www.linn.co.uk/klimax");
        vm.SerialNumber.Should().Be("SN-001");
        vm.Upc.Should().Be("—", "absent Upc renders as the muted placeholder");
    }

    // ── Network (AC-2.9.4) ──

    [Fact]
    [Trait("ac", "AC-2.9.4")]
    public void Network_MapsLocationServerCacheControl_AC294()
    {
        var entry = BuildEntry(
            location: new Uri("http://192.168.1.100:49152/desc.xml"),
            description: Description(),
            server: "Linux/3.0 UPnP/1.0",
            maxAge: TimeSpan.FromSeconds(1800));
        var vm = NewVm(entry);

        vm.LocationUrl.Should().Be("http://192.168.1.100:49152/desc.xml");
        vm.Ip.Should().Be("192.168.1.100");
        vm.Port.Should().Be("49152");
        vm.SsdpServer.Should().Be("Linux/3.0 UPnP/1.0");
        vm.CacheControlMaxAgeSeconds.Should().Be("1800");
    }

    [Fact]
    [Trait("ac", "AC-2.9.4")]
    public void Network_NullServer_RendersDash_AC294()
    {
        var entry = BuildEntry(description: Description(), server: null);
        var vm = NewVm(entry);

        vm.SsdpServer.Should().Be("—");
    }

    // ── Discovery history (AC-2.9.4) ──

    [Fact]
    [Trait("ac", "AC-2.9.4")]
    public void DiscoveryHistory_MapsTimestampsAndCounts_AC294()
    {
        var entry = BuildEntry(description: Description(), bootId: "99", configId: "3", aliveCount: 4);
        var vm = NewVm(entry);

        vm.AliveCount.Should().Be("4");
        vm.BootId.Should().Be("99");
        vm.ConfigId.Should().Be("3");
        // Timestamps render in the chosen "yyyy-MM-dd HH:mm:ss" local-time format.
        vm.FirstSeenUtc.Should().MatchRegex(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}$");
        vm.LastSeenUtc.Should().MatchRegex(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}$");
    }

    [Fact]
    [Trait("ac", "AC-2.9.4")]
    public void DiscoveryHistory_NullBootId_RendersDash_AC294()
    {
        var entry = BuildEntry(description: Description(), bootId: null);
        var vm = NewVm(entry);

        vm.BootId.Should().Be("—");
    }

    // ── Absent vs empty (AC-2.9.4) ──

    [Fact]
    [Trait("ac", "AC-2.9.4")]
    public void AbsentFields_RenderAsDash_AC294()
    {
        // A minimal description with all-nullable fields null + a present-but-empty string.
        var desc = new DeviceDescription(
            FriendlyName: "Dev", DeviceType: "urn:x", Udn: "uuid:x",
            PresentationUrl: null, Manufacturer: "", ManufacturerUrl: null,
            ModelName: "", ModelNumber: null, ModelDescription: null, ModelUrl: null,
            SerialNumber: null, Upc: null, Services: Array.Empty<ServiceDescription>());
        var entry = BuildEntry(description: desc);
        var vm = NewVm(entry);

        vm.PresentationUrl.Should().Be("—");
        vm.ManufacturerUrl.Should().Be("—");
        vm.ModelNumber.Should().Be("—");
        vm.ModelDescription.Should().Be("—");
        vm.ModelUrl.Should().Be("—");
        vm.SerialNumber.Should().Be("—");
        vm.Upc.Should().Be("—");
        vm.Manufacturer.Should().Be("—", "a present-but-empty string is also rendered as the dash");
        vm.ModelName.Should().Be("—");
    }

    // ── Embedded devices (AC-2.9.4 / Decision 5) ──

    [Fact]
    [Trait("ac", "AC-2.9.4")]
    public void EmbeddedDevices_AlwaysEmpty_AC294()
    {
        var entry = BuildEntry(description: Description());
        var vm = NewVm(entry);

        vm.EmbeddedDevices.Should().BeEmpty("the model flattens embedded devices per FR-053 (Decision 5)");
        vm.HasEmbeddedDevices.Should().BeFalse();
    }

    // ── Device-gone banner (AC-2.9.6) ──

    [Fact]
    [Trait("ac", "AC-2.9.6")]
    public void DeviceRemoved_MatchingUuid_SetsBanner_AC296()
    {
        var udn = $"uuid:{Guid.NewGuid()}";
        var registry = new FakeDeviceRegistry();
        var entry = BuildEntry(registryUdn: udn, description: Description(friendlyName: "Snapshot Name"));
        var vm = NewVm(entry, registry: registry);

        registry.RaiseDeviceRemoved(udn);

        vm.IsDeviceGone.Should().BeTrue();
        vm.DeviceGoneText.Should().StartWith("Device left the network");
        vm.FriendlyName.Should().Be("Snapshot Name", "snapshot data stays visible after removal");
    }

    [Fact]
    [Trait("ac", "AC-2.9.6")]
    public void DeviceRemoved_OtherUuid_Ignored_AC296()
    {
        var registry = new FakeDeviceRegistry();
        var entry = BuildEntry(registryUdn: $"uuid:{Guid.NewGuid()}", description: Description());
        var vm = NewVm(entry, registry: registry);

        registry.RaiseDeviceRemoved($"uuid:{Guid.NewGuid()}"); // a DIFFERENT device

        vm.IsDeviceGone.Should().BeFalse();
    }

    // Amendment A30 regression (f): the FR-037 banner flips on a DIFFERENT-CASED string UDN
    // (OrdinalIgnoreCase match) and a non-GUID UDN; a different UDN does not flip it.
    [Fact]
    [Trait("ac", "AC-2.4.1")]
    public void DeviceRemoved_DifferentCasedNonGuidUdn_FlipsBanner_OrdinalIgnoreCase()
    {
        var registry = new FakeDeviceRegistry();
        var entry = BuildEntry(registryUdn: "uuid:linn-ds-0001", description: Description());
        var vm = NewVm(entry, registry: registry);

        registry.RaiseDeviceRemoved("uuid:LINN-DS-0001"); // same device, different case

        vm.IsDeviceGone.Should().BeTrue("OrdinalIgnoreCase UDN match flips the FR-037 banner");
    }

    // ── Hyperlink open (AC-2.9.5) ──

    [Fact]
    [Trait("ac", "AC-2.9.5")]
    public void OpenUrlCommand_HttpUri_Launches_AC295()
    {
        var launcher = new FakeUriLauncher();
        var entry = BuildEntry(description: Description());
        var vm = NewVm(entry, launcher: launcher);
        var uri = vm.PresentationUri;

        vm.OpenUrlCommand.Execute(uri);

        launcher.Launched.Should().ContainSingle().Which.Should().Be(uri);
    }

    [Fact]
    [Trait("ac", "AC-2.9.5")]
    public void OpenUrlCommand_NullUri_NoLaunch_NoThrow_AC295()
    {
        var launcher = new FakeUriLauncher();
        var diag = new CapturingDiagnosticEmitter();
        var vm = NewVm(BuildEntry(description: Description()), launcher: launcher, diag: diag);

        var act = () => vm.OpenUrlCommand.Execute(null);

        act.Should().NotThrow();
        launcher.Launched.Should().BeEmpty();
        diag.Entries.Should().BeEmpty("an absent URL is a silent no-op");
    }

    [Fact]
    [Trait("ac", "AC-2.9.5")]
    public void OpenUrlCommand_NonHttpUri_Refused_Warns_AC295()
    {
        var launcher = new FakeUriLauncher();
        var diag = new CapturingDiagnosticEmitter();
        var vm = NewVm(BuildEntry(description: Description()), launcher: launcher, diag: diag);

        vm.OpenUrlCommand.Execute(new Uri("file:///etc/passwd"));

        launcher.Launched.Should().BeEmpty("the Story 2.8 whitelist refuses non-http(s)");
        diag.Entries.Should().ContainSingle()
            .Which.Category.Should().Be(DiagCategories.ShellExecute);
    }

    [Fact]
    [Trait("ac", "AC-2.9.5")]
    public void PresentationUri_RelativeResolvedAgainstLocation_AC295()
    {
        var entry = BuildEntry(
            location: new Uri("http://host:80/desc.xml"),
            description: Description(presentationUrl: "/index.html"));
        var vm = NewVm(entry);

        vm.PresentationUri.Should().Be(new Uri("http://host:80/index.html"));
    }

    // ── Dispose / unsubscribe (AC-2.9.6) ──

    [Fact]
    [Trait("ac", "AC-2.9.6")]
    public void Dispose_Unsubscribes_NoBannerAfterDispose_AC296()
    {
        var udn = $"uuid:{Guid.NewGuid()}";
        var registry = new FakeDeviceRegistry();
        var vm = NewVm(BuildEntry(registryUdn: udn, description: Description()), registry: registry);

        vm.Dispose();
        registry.RaiseDeviceRemoved(udn);

        vm.IsDeviceGone.Should().BeFalse("Dispose detaches the DeviceRemoved handler");
    }
}
