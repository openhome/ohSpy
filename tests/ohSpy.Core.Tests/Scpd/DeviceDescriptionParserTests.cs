namespace ohSpy.Core.Tests.Scpd;

using System.Text;
using FluentAssertions;
using ohSpy.Core.Http;
using ohSpy.Core.Scpd;

/// <summary>
/// Story 1.4 tests for <see cref="DeviceDescriptionParser"/> — AC-9 (metadata extraction,
/// FR-053 flattening) + AC-5.3 (XXE defence applies here too) + null/empty input safety.
/// </summary>
public sealed class DeviceDescriptionParserTests
{
    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "DeviceDescriptions", name);

    private static DeviceDescriptionParser NewParser() => new DeviceDescriptionParser();

    [Fact]
    public void Parse_TypicalLinnDs_ExtractsAllMetadata()
    {
        var bytes = File.ReadAllBytes(FixturePath("linn-ds.xml"));
        var parser = NewParser();

        var dd = parser.Parse(bytes);

        dd.FriendlyName.Should().Be("Living Room DS");
        dd.DeviceType.Should().Be("urn:linn-co-uk:device:Source:1");
        dd.Udn.Should().Be("uuid:4c494e4e-0000-0000-0000-000000000001");
        dd.Manufacturer.Should().Be("Linn Products");
        dd.ManufacturerUrl.Should().Be("http://www.linn.co.uk");
        dd.ModelDescription.Should().Be("Linn Klimax DS");
        dd.ModelName.Should().Be("Klimax DS");
        dd.ModelNumber.Should().Be("3.0");
        dd.ModelUrl.Should().Be("http://www.linn.co.uk/klimax-ds");
        dd.SerialNumber.Should().Be("123456");
        dd.Upc.Should().Be("0123456789012");
        dd.PresentationUrl.Should().Be("http://192.168.1.100/");

        dd.Services.Should().ContainSingle();
        var svc = dd.Services[0];
        svc.ServiceType.Should().Be("urn:linn-co-uk:service:Volkano:1");
        svc.ServiceId.Should().Be("urn:linn-co-uk:serviceId:Volkano");
        svc.ScpdUrl.Should().Be("/Volkano/Scpd.xml");
        svc.ControlUrl.Should().Be("/Volkano/control");
        svc.EventSubUrl.Should().Be("/Volkano/event");
    }

    [Fact]
    public void Parse_MinimalDescription_LeavesOptionalFieldsNull()
    {
        var bytes = File.ReadAllBytes(FixturePath("minimal.xml"));
        var parser = NewParser();

        var dd = parser.Parse(bytes);

        dd.FriendlyName.Should().Be("Minimal");
        dd.DeviceType.Should().Be("urn:test:device:Minimal:1");
        dd.Udn.Should().Be("uuid:minimal-0000-0000-0000-000000000001");
        dd.Manufacturer.Should().Be("Test");
        dd.ModelName.Should().Be("Min");

        dd.PresentationUrl.Should().BeNull();
        dd.ManufacturerUrl.Should().BeNull();
        dd.ModelNumber.Should().BeNull();
        dd.ModelDescription.Should().BeNull();
        dd.ModelUrl.Should().BeNull();
        dd.SerialNumber.Should().BeNull();
        dd.Upc.Should().BeNull();

        dd.Services.Should().BeEmpty();
    }

    [Fact]
    [Trait("fr", "FR-053")]
    public void Parse_IgdWithEmbeddedDevices_FlattensServicesPerFr053()
    {
        var bytes = File.ReadAllBytes(FixturePath("igd-with-embedded.xml"));
        var parser = NewParser();

        var dd = parser.Parse(bytes);

        // Root metadata wins — embedded devices' FriendlyName / Manufacturer never appear.
        dd.Udn.Should().Be("uuid:igd-root-0000-0000-000000000001");
        dd.FriendlyName.Should().Be("Home Router");
        dd.Manufacturer.Should().Be("Netgear");
        dd.ModelName.Should().Be("R7000");

        // FR-053 flattening: 3 services in source-document order (root, WAN, WAN-Conn).
        dd.Services.Should().HaveCount(3);
        dd.Services.Select(s => s.ServiceType).Should().Equal(
            "urn:schemas-upnp-org:service:Layer3Forwarding:1",
            "urn:schemas-upnp-org:service:WANCommonInterfaceConfig:1",
            "urn:schemas-upnp-org:service:WANIPConnection:1");

        // No trace of embedded devices' friendly names anywhere in the result.
        dd.FriendlyName.Should().NotContain("WAN");
    }

    [Fact]
    [Trait("ac", "AC-5.3")]
    public void Parse_XxeAttempt_ThrowsUpnpProtocolException()
    {
        // Reuse the SCPD XXE pattern but adapted to device description schema.
        var xml = Encoding.UTF8.GetBytes(
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE root [
              <!ENTITY xxe SYSTEM "file:///etc/passwd">
            ]>
            <root xmlns="urn:schemas-upnp-org:device-1-0">
              <device>
                <friendlyName>Stolen&xxe;</friendlyName>
              </device>
            </root>
            """);
        var parser = NewParser();

        var act = () => parser.Parse(xml);
        act.Should().Throw<UpnpProtocolException>();
    }

    [Fact]
    public void Parse_NullInput_ThrowsArgumentNullException()
    {
        var parser = NewParser();
        var act = () => parser.Parse(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Parse_EmptyByteArray_ThrowsUpnpProtocolException()
    {
        var parser = NewParser();
        var act = () => parser.Parse(Array.Empty<byte>());
        act.Should().Throw<UpnpProtocolException>();
    }

    [Fact]
    public void Parse_NoRootDevice_ThrowsUpnpProtocolException()
    {
        var xml = Encoding.UTF8.GetBytes(
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <root xmlns="urn:schemas-upnp-org:device-1-0">
              <specVersion><major>1</major><minor>0</minor></specVersion>
            </root>
            """);
        var parser = NewParser();

        var act = () => parser.Parse(xml);
        act.Should().Throw<UpnpProtocolException>();
    }
}
