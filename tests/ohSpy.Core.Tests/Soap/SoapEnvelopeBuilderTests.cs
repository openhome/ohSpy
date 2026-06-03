namespace ohSpy.Core.Tests.Soap;

using FluentAssertions;
using ohSpy.Core.Models;
using ohSpy.Core.Soap;

public class SoapEnvelopeBuilderTests
{
    private static readonly Uri ControlUrl = new("http://192.0.2.10:49152/AVTransport/control");
    private const string ServiceType = "urn:schemas-upnp-org:service:AVTransport:1";

    // Pinned-string golden assertions (AC-3.1.6 #21). The repo's house style is inline XML in
    // tests; these strings are the verbatim XmlWriter output (captured, not hand-guessed) — so
    // they double as a regression guard on framing, namespace placement, and escaping.

    [Fact]
    [Trait("ac", "AC-3.1.2")]
    public void Build_ZeroArgs_ProducesSelfClosingActionElement()
    {
        // FR-031: argument-less action ⇒ no children ⇒ self-closing <u:Action ... />.
        var req = new SoapRequest(ControlUrl, ServiceType, "GetTransportInfo", Array.Empty<SoapArgument>());

        var xml = SoapEnvelopeBuilder.Build(req);

        xml.Should().Be(
            "<s:Envelope s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\" " +
            "xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\">" +
            "<s:Body>" +
            "<u:GetTransportInfo xmlns:u=\"urn:schemas-upnp-org:service:AVTransport:1\" />" +
            "</s:Body></s:Envelope>");
        // No <?xml ...?> declaration is emitted (avoids the UTF-16 declaration trap).
        xml.Should().NotContain("<?xml");
    }

    [Fact]
    [Trait("ac", "AC-3.1.2")]
    public void Build_OneArg_RendersChildElement()
    {
        var req = new SoapRequest(ControlUrl, ServiceType, "SetVolume",
            new[] { new SoapArgument("InstanceID", "0") });

        var xml = SoapEnvelopeBuilder.Build(req);

        xml.Should().Be(
            "<s:Envelope s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\" " +
            "xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\">" +
            "<s:Body>" +
            "<u:SetVolume xmlns:u=\"urn:schemas-upnp-org:service:AVTransport:1\">" +
            "<InstanceID>0</InstanceID>" +
            "</u:SetVolume>" +
            "</s:Body></s:Envelope>");
    }

    [Fact]
    [Trait("ac", "AC-3.1.2")]
    public void Build_MultipleArgs_PreservesOrder()
    {
        var req = new SoapRequest(ControlUrl, ServiceType, "Browse", new[]
        {
            new SoapArgument("ObjectID", "0"),
            new SoapArgument("BrowseFlag", "BrowseDirectChildren"),
            new SoapArgument("StartingIndex", "0"),
        });

        var xml = SoapEnvelopeBuilder.Build(req);

        xml.Should().Be(
            "<s:Envelope s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\" " +
            "xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\">" +
            "<s:Body>" +
            "<u:Browse xmlns:u=\"urn:schemas-upnp-org:service:AVTransport:1\">" +
            "<ObjectID>0</ObjectID>" +
            "<BrowseFlag>BrowseDirectChildren</BrowseFlag>" +
            "<StartingIndex>0</StartingIndex>" +
            "</u:Browse>" +
            "</s:Body></s:Envelope>");
    }

    [Fact]
    [Trait("ac", "AC-3.1.2")]
    public void Build_AdversarialValue_EscapesMarkupCharacters()
    {
        // AC #7 — assert the ACTUAL XmlWriter output: < > & → entities; " and ' are NOT escaped
        // in element text (spec-correct; XmlWriter only escapes them inside attribute values).
        var req = new SoapRequest(ControlUrl, ServiceType, "SetText",
            new[] { new SoapArgument("Value", "a<b>c&d\"e'f") });

        var xml = SoapEnvelopeBuilder.Build(req);

        xml.Should().Be(
            "<s:Envelope s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\" " +
            "xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\">" +
            "<s:Body>" +
            "<u:SetText xmlns:u=\"urn:schemas-upnp-org:service:AVTransport:1\">" +
            "<Value>a&lt;b&gt;c&amp;d\"e'f</Value>" +
            "</u:SetText>" +
            "</s:Body></s:Envelope>");
    }
}
