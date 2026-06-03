namespace ohSpy.Core.Tests.ViewModels;

using FluentAssertions;
using ohSpy.Core.Devices;
using ohSpy.Core.Models;
using ohSpy.Core.Tests.Fakes;
using ohSpy.Core.ViewModels;

/// <summary>
/// Story 2.5 — <see cref="DeviceTreeViewModel"/> unit tests.
/// Covers AC-2.5.3, AC-2.5.8, AC-2.5.9.
/// Uses real <see cref="DeviceRegistry"/> + <see cref="InlineUiDispatcher"/> for synchronous dispatch.
/// </summary>
public sealed class DeviceTreeViewModelTests : IDisposable
{
    private static readonly Uri Location = new("http://192.168.1.1:49152/desc.xml");

    private readonly InlineUiDispatcher _ui = new();
    private readonly DeviceRegistry _registry;
    private readonly DeviceTreeViewModel _vm;
    private readonly NodeServices _nodeServices;

    public DeviceTreeViewModelTests()
    {
        _registry = new DeviceRegistry(_ui);
        _nodeServices = new NodeServices(
            new StubUpnpHttpClient(), new StubScpdParser(), _ui, new CapturingDiagnosticEmitter());
        _vm = new DeviceTreeViewModel(_registry, _ui, _nodeServices);
    }

    public void Dispose() => _vm.Dispose();

    private RegistryEntry AddLoadedDevice(Guid uuid, string friendlyName = "Test Device",
        string deviceType = "urn:schemas-upnp-org:device:Basic:1")
    {
        // Use OnAlive so the entry lands in _entries (required for OnByebye / Remove to find it).
        _registry.OnAlive(uuid, Location, DateTime.UtcNow, "Test/1.0",
            TimeSpan.FromSeconds(1800), "1", "1", CancellationToken.None);
        _registry.TryGetEntry(uuid, out var entry);
        entry.MarkInFlight();
        entry.MarkLoaded(new DeviceDescription(
            friendlyName, deviceType, $"uuid:{uuid}",
            null, "Mfr", null, "Model",
            null, null, null, null, null,
            Array.Empty<ServiceDescription>()));
        _registry.RaiseDeviceLoaded(entry);
        return entry;
    }

    [Fact]
    [Trait("ac", "AC-2.5.3")]
    public void DeviceLoaded_AddsDeviceNodeViewModelToDevices_AC253()
    {
        var uuid = Guid.NewGuid();

        AddLoadedDevice(uuid);

        _vm.Devices.Count.Should().Be(1);
        _vm.Devices[0].Uuid.Should().Be(uuid);
    }

    [Fact]
    [Trait("ac", "AC-2.5.3")]
    public void DeviceLoaded_DuplicateUuid_TreatedAsUpdate_NoThrow_AC253()
    {
        // Review patch P1: a second DeviceLoaded for a known UUID must not throw
        // (IdentityKeyedSortedCollection.Add throws on duplicate). It is folded into an update.
        var uuid = Guid.NewGuid();
        AddLoadedDevice(uuid, "First Name");

        var second = new RegistryEntry(uuid, Location, DateTime.UtcNow, CancellationToken.None);
        second.MarkInFlight();
        second.MarkLoaded(new DeviceDescription(
            "Second Name", "urn:schemas-upnp-org:device:Basic:1", $"uuid:{uuid}",
            null, "Mfr", null, "Model",
            null, null, null, null, null,
            Array.Empty<ServiceDescription>()));

        var act = () => _registry.RaiseDeviceLoaded(second);

        act.Should().NotThrow();
        _vm.Devices.Count.Should().Be(1, "duplicate Loaded must not add a second row");
        _vm.Devices[0].FriendlyName.Should().Be("Second Name", "duplicate Loaded refreshes display");
    }

    [Fact]
    [Trait("ac", "AC-2.5.3")]
    public void DeviceLoaded_MarshalledViaDispatcher_AC253()
    {
        var posted = false;
        var recordingUi = new RecordingUiDispatcher(() => posted = true);
        var registry = new DeviceRegistry(new InlineUiDispatcher());
        var vm = new DeviceTreeViewModel(registry, recordingUi, _nodeServices);

        var entry = new RegistryEntry(Guid.NewGuid(), Location, DateTime.UtcNow, CancellationToken.None);
        entry.MarkInFlight();
        entry.MarkLoaded(new DeviceDescription(
            "Test", "urn:schemas-upnp-org:device:Basic:1", "uuid:test",
            null, "Mfr", null, "Model",
            null, null, null, null, null,
            Array.Empty<ServiceDescription>()));
        registry.RaiseDeviceLoaded(entry);

        posted.Should().BeTrue("DeviceLoaded handler must marshal via IUiDispatcher.Post");
    }

    [Fact]
    [Trait("ac", "AC-2.5.3")]
    public void DeviceRemoved_RemovesFromDevices_AC253()
    {
        var uuid = Guid.NewGuid();
        AddLoadedDevice(uuid);

        _registry.OnByebye(uuid);

        _vm.Devices.Count.Should().Be(0);
    }

    [Fact]
    [Trait("ac", "AC-2.5.8")]
    public void DeviceUpdated_UpdatesExistingNodeAndResortsIfNeeded_AC258()
    {
        var uuid1 = Guid.Parse("00000001-0000-0000-0000-000000000000");
        var uuid2 = Guid.Parse("00000002-0000-0000-0000-000000000000");
        AddLoadedDevice(uuid1, "Bravo");
        AddLoadedDevice(uuid2, "Alpha");

        // Rename "Bravo" to "Aardvark" — should sort before "Alpha"
        var updatedEntry = new RegistryEntry(uuid1, Location, DateTime.UtcNow, CancellationToken.None);
        updatedEntry.MarkInFlight();
        updatedEntry.MarkLoaded(new DeviceDescription(
            "Aardvark", "urn:schemas-upnp-org:device:Basic:1", $"uuid:{uuid1}",
            null, "Mfr", null, "Model",
            null, null, null, null, null,
            Array.Empty<ServiceDescription>()));
        _registry.RaiseDeviceUpdated(updatedEntry);

        _vm.Devices[0].FriendlyName.Should().Be("Aardvark", "Aardvark should sort before Alpha");
        _vm.Devices[1].FriendlyName.Should().Be("Alpha");
    }

    [Fact]
    [Trait("ac", "AC-2.5.8")]
    public void DeviceUpdated_PreservesNodeIdentityOnRename_AC258()
    {
        var uuid = Guid.NewGuid();
        AddLoadedDevice(uuid, "Old Name");
        var originalNode = _vm.Devices[0];

        var updatedEntry = new RegistryEntry(uuid, Location, DateTime.UtcNow, CancellationToken.None);
        updatedEntry.MarkInFlight();
        updatedEntry.MarkLoaded(new DeviceDescription(
            "New Name", "urn:schemas-upnp-org:device:Basic:1", $"uuid:{uuid}",
            null, "Mfr", null, "Model",
            null, null, null, null, null,
            Array.Empty<ServiceDescription>()));
        _registry.RaiseDeviceUpdated(updatedEntry);

        _vm.Devices[0].Should().BeSameAs(originalNode,
            "update must reuse the existing VM instance (FR-054 Move semantics)");
        _vm.Devices[0].FriendlyName.Should().Be("New Name");
    }

    [Fact]
    [Trait("ac", "AC-2.5.3")]
    public void Devices_SortedCaseInsensitive_AC253()
    {
        AddLoadedDevice(Guid.NewGuid(), "zebra");
        AddLoadedDevice(Guid.NewGuid(), "Apple");

        _vm.Devices[0].FriendlyName.Should().Be("Apple");
        _vm.Devices[1].FriendlyName.Should().Be("zebra");
    }

    [Fact]
    [Trait("ac", "AC-2.5.3")]
    public void Devices_UuidTiebreakForEqualFriendlyNames_AC253()
    {
        var uuid1 = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000000");
        var uuid2 = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000000");
        AddLoadedDevice(uuid2, "Linn DS");
        AddLoadedDevice(uuid1, "Linn DS");

        // uuid1 (aaaa...) sorts before uuid2 (bbbb...) lexicographically
        _vm.Devices[0].Uuid.Should().Be(uuid1);
        _vm.Devices[1].Uuid.Should().Be(uuid2);
    }

    [Fact]
    [Trait("ac", "AC-2.5.3")]
    public void DeviceRemoved_UnknownUuid_DoesNotThrow_AC253()
    {
        var act = () => _registry.OnByebye(Guid.NewGuid());

        act.Should().NotThrow("remove of unknown UUID must be a no-op");
    }

    // Minimal recording dispatcher to verify that Post is called.
    private sealed class RecordingUiDispatcher(Action onPost) : ohSpy.Core.Threading.IUiDispatcher
    {
        public bool IsOnUiThread => true;
        public void Post(Action action) { onPost(); action(); }
        public Task<T> PostAsync<T>(Func<T> readback) => Task.FromResult(readback());
        public void AssertOnUiThread() { }
    }
}
