namespace ohSpy.Core.Tests.Discovery;

using System.Text;
using FluentAssertions;
using ohSpy.Core.Diagnostics;
using ohSpy.Core.Discovery;
using ohSpy.Core.Tests.Fakes;
using Xunit;

public sealed class SsdpParserTests
{
    private const string TestGuidBody = "f7dc20e5-1234-5678-abcd-ef0123456789";
    private const string TestUdn = "uuid:" + TestGuidBody;

    private static SsdpParser MakeParser(out CapturingDiagnosticEmitter cap)
    {
        cap = new CapturingDiagnosticEmitter();
        return new SsdpParser(cap);
    }

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    // ── happy-path tests ────────────────────────────────────────────────────

    [Fact]
    [Trait("ac", "AC-2.4.2")]
    public void Parse_Notify_Alive_RootDevice_ExtractsAllFields_AC242()
    {
        var parser = MakeParser(out var cap);
        var payload = Bytes(
            $"NOTIFY * HTTP/1.1\r\n" +
            $"HOST: 239.255.255.250:1900\r\n" +
            $"NT: upnp:rootdevice\r\n" +
            $"NTS: ssdp:alive\r\n" +
            $"USN: uuid:{TestGuidBody}::upnp:rootdevice\r\n" +
            $"LOCATION: http://192.0.2.1:49152/desc.xml\r\n" +
            $"CACHE-CONTROL: max-age=1800\r\n" +
            $"SERVER: Linux/1.0 UPnP/1.1 TestDevice/1.0\r\n" +
            $"BOOTID.UPNP.ORG: 42\r\n" +
            $"CONFIGID.UPNP.ORG: 7\r\n" +
            $"\r\n");

        var ann = parser.Parse(payload, "192.0.2.1:1900");

        ann.Should().NotBeNull();
        ann!.NT.Should().Be("upnp:rootdevice");
        ann.NTS.Should().Be("ssdp:alive");
        ann.Udn.Should().Be(TestUdn);
        ann.Location.Should().Be(new Uri("http://192.0.2.1:49152/desc.xml"));
        ann.CacheControlMaxAge.Should().Be(TimeSpan.FromSeconds(1800));
        ann.Server.Should().Be("Linux/1.0 UPnP/1.1 TestDevice/1.0");
        ann.BootId.Should().Be("42");
        ann.ConfigId.Should().Be("7");
        ann.IsRootDevice.Should().BeTrue();
        cap.Entries.Should().BeEmpty();
    }

    [Fact]
    [Trait("ac", "AC-2.4.1")]
    public void Parse_Notify_Alive_EmbeddedDevice_IsRootDeviceFalse_AC241()
    {
        var parser = MakeParser(out var cap);
        var payload = Bytes(
            $"NOTIFY * HTTP/1.1\r\n" +
            $"NT: urn:schemas-upnp-org:device:MediaRenderer:1\r\n" +
            $"NTS: ssdp:alive\r\n" +
            $"USN: uuid:{TestGuidBody}::urn:schemas-upnp-org:device:MediaRenderer:1\r\n" +
            $"\r\n");

        var ann = parser.Parse(payload, "192.0.2.1:1900");

        ann.Should().NotBeNull();
        ann!.IsRootDevice.Should().BeFalse();
        cap.Entries.Should().BeEmpty();
    }

    [Fact]
    [Trait("ac", "AC-2.4.2")]
    public void Parse_Notify_Byebye_AC242()
    {
        var parser = MakeParser(out var cap);
        var payload = Bytes(
            $"NOTIFY * HTTP/1.1\r\n" +
            $"NT: upnp:rootdevice\r\n" +
            $"NTS: ssdp:byebye\r\n" +
            $"USN: uuid:{TestGuidBody}::upnp:rootdevice\r\n" +
            $"\r\n");

        var ann = parser.Parse(payload, "192.0.2.1:1900");

        ann.Should().NotBeNull();
        ann!.NTS.Should().Be("ssdp:byebye");
        ann.NT.Should().Be("upnp:rootdevice");
        ann.Udn.Should().Be(TestUdn);
        cap.Entries.Should().BeEmpty();
    }

    [Fact]
    [Trait("ac", "AC-2.4.2")]
    public void Parse_MSearchResponse_200OK_AC242()
    {
        var parser = MakeParser(out var cap);
        var payload = Bytes(
            $"HTTP/1.1 200 OK\r\n" +
            $"ST: upnp:rootdevice\r\n" +
            $"USN: uuid:{TestGuidBody}::upnp:rootdevice\r\n" +
            $"LOCATION: http://192.0.2.1:49152/desc.xml\r\n" +
            $"CACHE-CONTROL: max-age=1800\r\n" +
            $"\r\n");

        var ann = parser.Parse(payload, "192.0.2.1:12345");

        ann.Should().NotBeNull();
        ann!.ST.Should().Be("upnp:rootdevice");
        ann.NT.Should().BeNull();
        ann.NTS.Should().BeNull();
        ann.Udn.Should().Be(TestUdn);
        cap.Entries.Should().BeEmpty();
    }

    [Fact]
    [Trait("ac", "AC-2.4.2")]
    public void Parse_UnknownHeaders_Ignored_AC242()
    {
        var parser = MakeParser(out var cap);
        var payload = Bytes(
            "NOTIFY * HTTP/1.1\r\n" +
            "NT: upnp:rootdevice\r\n" +
            "NTS: ssdp:alive\r\n" +
            "X-VENDOR-EXTENSION: foo\r\n" +
            "CUSTOM-HEADER: bar\r\n" +
            "\r\n");

        var act = () => parser.Parse(payload, "remote");

        act.Should().NotThrow();
        var ann = parser.Parse(payload, "remote");
        ann.Should().NotBeNull();
        cap.Entries.Should().BeEmpty();
    }

    // ── malformed tests ─────────────────────────────────────────────────────

    [Fact]
    [Trait("ac", "AC-2.4.3")]
    public void Parse_Malformed_EmptyPayload_ReturnsNull_EmitsWarning_AC243()
    {
        var parser = MakeParser(out var cap);

        var result = parser.Parse([], "192.0.2.1:1900");

        result.Should().BeNull();
        cap.Entries.Should().ContainSingle(e =>
            e.Severity == "Warning" &&
            e.Category == DiagCategories.SsdpParse &&
            e.Context.RemoteEndpoint == "192.0.2.1:1900");
    }

    [Fact]
    [Trait("ac", "AC-2.4.3")]
    public void Parse_Malformed_NoFirstLine_ReturnsNull_AC243()
    {
        var parser = MakeParser(out var cap);
        var payload = Bytes("GARBAGE FIRST LINE\r\nNT: something\r\n\r\n");

        var result = parser.Parse(payload, "192.0.2.42:1900");

        result.Should().BeNull();
        cap.Entries.Should().ContainSingle(e =>
            e.Severity == "Warning" && e.Category == DiagCategories.SsdpParse);
    }

    // ── ExtractUdn tests (Amendment A30 — the UDN is an OPAQUE string; NO Guid parse) ──────────

    [Theory]
    [Trait("ac", "AC-2.4.1")]
    // Full uuid:<body> kept (prefix retained), suffix stripped, body casing preserved:
    [InlineData("uuid:f7dc20e5-1234-5678-abcd-ef0123456789", "uuid:f7dc20e5-1234-5678-abcd-ef0123456789")]
    [InlineData("uuid:f7dc20e5-1234-5678-abcd-ef0123456789::upnp:rootdevice", "uuid:f7dc20e5-1234-5678-abcd-ef0123456789")]
    [InlineData("UUID:F7DC20E5-1234-5678-ABCD-EF0123456789", "UUID:F7DC20E5-1234-5678-ABCD-EF0123456789")]
    // (a) THE REGRESSION: a non-RFC-4122 UDN is returned VERBATIM (no parse, no null):
    [InlineData("uuid:linn-ds-akurate-0001::upnp:rootdevice", "uuid:linn-ds-akurate-0001")]
    [InlineData("uuid:4c494e4e-NOT-hex", "uuid:4c494e4e-NOT-hex")]
    [InlineData("uuid:linn-ds-akurate-0001", "uuid:linn-ds-akurate-0001")]
    // No uuid: token → null (the ONLY null cases):
    [InlineData("f7dc20e5-1234-5678-abcd-ef0123456789", null)]
    [InlineData("not-a-uuid", null)]
    [InlineData(null, null)]
    public void ExtractUdn_HandlesAllForms_AC241(string? usn, string? expectedUdn)
    {
        var result = SsdpParser.ExtractUdn(usn);

        result.Should().Be(expectedUdn);
    }

    // ── cache-control tests ──────────────────────────────────────────────────

    [Fact]
    [Trait("ac", "AC-2.4.2")]
    public void Parse_CacheControl_ParsesMaxAge_AC242()
    {
        var parser = MakeParser(out _);
        var payload = Bytes("NOTIFY * HTTP/1.1\r\nNT: upnp:rootdevice\r\nNTS: ssdp:alive\r\nCACHE-CONTROL: max-age=1800\r\n\r\n");

        var ann = parser.Parse(payload, "remote");

        ann!.CacheControlMaxAge.Should().Be(TimeSpan.FromSeconds(1800));
    }

    [Fact]
    [Trait("ac", "AC-2.4.2")]
    public void Parse_CacheControl_MissingMaxAge_ReturnsNull_AC242()
    {
        var parser = MakeParser(out _);
        var payload = Bytes("NOTIFY * HTTP/1.1\r\nNT: upnp:rootdevice\r\nNTS: ssdp:alive\r\nCACHE-CONTROL: no-cache\r\n\r\n");

        var ann = parser.Parse(payload, "remote");

        ann!.CacheControlMaxAge.Should().BeNull();
    }

    // ── location tests ───────────────────────────────────────────────────────

    [Fact]
    [Trait("ac", "AC-2.4.2")]
    public void Parse_Location_ParsesUri_AC242()
    {
        var parser = MakeParser(out _);
        var payload = Bytes("NOTIFY * HTTP/1.1\r\nNT: upnp:rootdevice\r\nNTS: ssdp:alive\r\nLOCATION: http://192.0.2.1:49152/desc.xml\r\n\r\n");

        var ann = parser.Parse(payload, "remote");

        ann!.Location.Should().Be(new Uri("http://192.0.2.1:49152/desc.xml"));
    }

    [Fact]
    [Trait("ac", "AC-2.4.2")]
    public void Parse_Location_InvalidUrl_ReturnsNull_AC242()
    {
        var parser = MakeParser(out _);
        var payload = Bytes("NOTIFY * HTTP/1.1\r\nNT: upnp:rootdevice\r\nNTS: ssdp:alive\r\nLOCATION: not-a-url\r\n\r\n");

        var ann = parser.Parse(payload, "remote");

        ann!.Location.Should().BeNull();
    }
}
