namespace ohSpy.Core.Tests.Discovery;

using FluentAssertions;
using ohSpy.Core.Diagnostics;
using ohSpy.Core.Devices;
using ohSpy.Core.Discovery;
using ohSpy.Core.Tests.Fakes;
using Xunit;

public sealed class DiscoveryServiceTests
{
    private static readonly Guid RootUuid = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid AnotherUuid = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    private static (DiscoveryService service, ChannelSsdpTransport transport,
        DeviceRegistry registry, CapturingDiagnosticEmitter cap)
        MakeSystem()
    {
        var cap = new CapturingDiagnosticEmitter();
        var ui = new InlineUiDispatcher();
        var transport = new ChannelSsdpTransport();
        var registry = new DeviceRegistry(ui);
        var parser = new SsdpParser(cap);
        var service = new DiscoveryService(registry, parser, ui);
        return (service, transport, registry, cap);
    }

    // Helper: write datagrams, complete channel, drain by awaiting DisposeAsync.
    private static async Task DrainAsync(DiscoveryService service, ChannelSsdpTransport transport)
    {
        transport.Complete();
        await service.DisposeAsync();
    }

    [Fact]
    [Trait("ac", "AC-2.4.5")]
    public async Task StartAsync_Alive_RootUuid_AddsEntryToRegistry_AC245()
    {
        var (service, transport, registry, _) = MakeSystem();
        var fetchFired = 0;
        registry.EntryNeedsFetch += _ => fetchFired++;

        await service.StartAsync(transport.IncomingDatagrams, CancellationToken.None, CancellationToken.None);
        await transport.WriteAsync(SsdpDatagramBuilder.Notify("upnp:rootdevice", "ssdp:alive", RootUuid));
        await DrainAsync(service, transport);

        registry.Count.Should().Be(1);
        fetchFired.Should().Be(1);
    }

    [Fact]
    [Trait("ac", "AC-2.4.6")]
    public async Task StartAsync_Alive_KnownUuid_RefreshesNoNewEntry_AC246()
    {
        var (service, transport, registry, _) = MakeSystem();

        await service.StartAsync(transport.IncomingDatagrams, CancellationToken.None, CancellationToken.None);
        await transport.WriteAsync(SsdpDatagramBuilder.Notify("upnp:rootdevice", "ssdp:alive", RootUuid));
        transport.Complete();
        await service.DisposeAsync();

        // Second pass with a fresh service (same registry)
        var transport2 = new ChannelSsdpTransport();
        var cap2 = new CapturingDiagnosticEmitter();
        var ui2 = new InlineUiDispatcher();
        var parser2 = new SsdpParser(cap2);
        var service2 = new DiscoveryService(registry, parser2, ui2);

        await service2.StartAsync(transport2.IncomingDatagrams, CancellationToken.None, CancellationToken.None);
        await transport2.WriteAsync(SsdpDatagramBuilder.Notify("upnp:rootdevice", "ssdp:alive", RootUuid));
        await DrainAsync(service2, transport2);

        registry.Count.Should().Be(1);
        registry.TryGetEntry(RootUuid, out var entry).Should().BeTrue();
        entry.AliveCount.Should().Be(2);
    }

    [Fact]
    [Trait("ac", "AC-2.4.7")]
    public async Task StartAsync_Byebye_KnownUuid_RemovesEntry_AC247()
    {
        var (service, transport, registry, _) = MakeSystem();
        Guid? removedUuid = null;
        registry.DeviceRemoved += id => removedUuid = id;

        await service.StartAsync(transport.IncomingDatagrams, CancellationToken.None, CancellationToken.None);
        await transport.WriteAsync(SsdpDatagramBuilder.Notify("upnp:rootdevice", "ssdp:alive", RootUuid));
        await transport.WriteAsync(SsdpDatagramBuilder.Notify("upnp:rootdevice", "ssdp:byebye", RootUuid));
        await DrainAsync(service, transport);

        registry.Count.Should().Be(0);
        removedUuid.Should().Be(RootUuid);
    }

    [Fact]
    [Trait("ac", "AC-2.4.7")]
    public async Task StartAsync_Byebye_UnknownUuid_RegistryUnchanged_AC247()
    {
        var (service, transport, registry, _) = MakeSystem();

        await service.StartAsync(transport.IncomingDatagrams, CancellationToken.None, CancellationToken.None);
        await transport.WriteAsync(SsdpDatagramBuilder.Notify("upnp:rootdevice", "ssdp:byebye", AnotherUuid));
        await DrainAsync(service, transport);

        registry.Count.Should().Be(0);
    }

    [Fact]
    [Trait("ac", "AC-2.4.8")]
    public async Task StartAsync_EmbeddedDevice_RegistryMuted_AnnouncementRaised_AC248()
    {
        var (service, transport, registry, _) = MakeSystem();
        var announced = 0;
        service.AnnouncementReceived += _ => announced++;

        await service.StartAsync(transport.IncomingDatagrams, CancellationToken.None, CancellationToken.None);
        await transport.WriteAsync(SsdpDatagramBuilder.Notify(
            "urn:schemas-upnp-org:device:MediaRenderer:1", "ssdp:alive", RootUuid));
        await DrainAsync(service, transport);

        registry.Count.Should().Be(0);
        announced.Should().Be(1);
    }

    [Fact]
    [Trait("ac", "AC-2.4.10")]
    public async Task StartAsync_MSearchResponse_TreatedAsAlive_AC2410()
    {
        var (service, transport, registry, _) = MakeSystem();

        await service.StartAsync(transport.IncomingDatagrams, CancellationToken.None, CancellationToken.None);
        await transport.WriteAsync(SsdpDatagramBuilder.SearchResponse(RootUuid));
        await DrainAsync(service, transport);

        registry.Count.Should().Be(1);
    }

    [Fact]
    [Trait("ac", "AC-2.4.3")]
    public async Task StartAsync_Malformed_EmitsWarning_RegistryUnchanged_AC243()
    {
        var (service, transport, registry, cap) = MakeSystem();
        var announced = 0;
        service.AnnouncementReceived += _ => announced++;

        await service.StartAsync(transport.IncomingDatagrams, CancellationToken.None, CancellationToken.None);
        await transport.WriteAsync(SsdpDatagramBuilder.Malformed());
        await DrainAsync(service, transport);

        registry.Count.Should().Be(0);
        announced.Should().Be(0);
        cap.Entries.Should().ContainSingle(e =>
            e.Severity == "Warning" && e.Category == DiagCategories.SsdpParse);
    }

    [Fact]
    [Trait("ac", "AC-2.4.4")]
    public async Task StartAsync_CancelToken_LoopExitsCleanly_AC244()
    {
        var (service, transport, _registry, _) = MakeSystem();
        using var cts = new CancellationTokenSource();

        await service.StartAsync(transport.IncomingDatagrams, cts.Token, CancellationToken.None);
        await transport.WriteAsync(SsdpDatagramBuilder.Notify("upnp:rootdevice", "ssdp:alive", RootUuid));
        await cts.CancelAsync();

        var disposeTask = service.DisposeAsync().AsTask();
        var completed = await Task.WhenAny(disposeTask, Task.Delay(200));
        completed.Should().Be(disposeTask, "DisposeAsync should complete within 200 ms after cancellation");
    }

    [Fact]
    [Trait("ac", "AC-2.4.9")]
    public async Task AnnouncementReceived_FiredForAllParsedAnnouncements_AC249()
    {
        var (service, transport, _registry, _) = MakeSystem();
        var announcements = new List<SsdpAnnouncement>();
        service.AnnouncementReceived += a => announcements.Add(a);

        await service.StartAsync(transport.IncomingDatagrams, CancellationToken.None, CancellationToken.None);
        // root alive
        await transport.WriteAsync(SsdpDatagramBuilder.Notify("upnp:rootdevice", "ssdp:alive", RootUuid));
        // embedded alive
        await transport.WriteAsync(SsdpDatagramBuilder.Notify(
            "urn:schemas-upnp-org:device:MediaRenderer:1", "ssdp:alive", AnotherUuid));
        // root byebye
        await transport.WriteAsync(SsdpDatagramBuilder.Notify("upnp:rootdevice", "ssdp:byebye", RootUuid));
        // malformed — should NOT raise event
        await transport.WriteAsync(SsdpDatagramBuilder.Malformed());
        await DrainAsync(service, transport);

        announcements.Should().HaveCount(3, "malformed datagram must not raise AnnouncementReceived");
    }
}
