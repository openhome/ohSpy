namespace ohSpy.Core.Tests.Soap;

using System.Text;
using FluentAssertions;
using ohSpy.Core.Soap;

public class SoapFaultParserTests
{
    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    private const string ValidFault = """
        <?xml version="1.0"?>
        <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/" s:encodingStyle="http://schemas.xmlsoap.org/soap/encoding/">
          <s:Body>
            <s:Fault>
              <faultcode>s:Client</faultcode>
              <faultstring>UPnPError</faultstring>
              <detail>
                <UPnPError xmlns="urn:schemas-upnp-org:control-1-0">
                  <errorCode>402</errorCode>
                  <errorDescription>Invalid Args</errorDescription>
                </UPnPError>
              </detail>
            </s:Fault>
          </s:Body>
        </s:Envelope>
        """;

    [Fact]
    [Trait("ac", "AC-3.1.3")]
    public void TryParse_ValidFault_ReturnsTrueWithParsedValues()
    {
        var ok = SoapFaultParser.TryParse(Bytes(ValidFault), out var fault);

        ok.Should().BeTrue();
        fault.ErrorCode.Should().Be(402);
        fault.ErrorDescription.Should().Be("Invalid Args");
    }

    [Fact]
    [Trait("ac", "AC-3.1.3")]
    public void TryParse_MissingErrorDescription_StillTrueWithEmptyDescription()
    {
        const string xml = """
            <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">
              <s:Body><s:Fault><detail><UPnPError xmlns="urn:schemas-upnp-org:control-1-0">
                <errorCode>714</errorCode>
              </UPnPError></detail></s:Fault></s:Body>
            </s:Envelope>
            """;

        var ok = SoapFaultParser.TryParse(Bytes(xml), out var fault);

        ok.Should().BeTrue();
        fault.ErrorCode.Should().Be(714);
        fault.ErrorDescription.Should().Be(string.Empty);
    }

    [Fact]
    [Trait("ac", "AC-3.1.3")]
    public void TryParse_MissingErrorCode_ReturnsFalse()
    {
        const string xml = """
            <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">
              <s:Body><s:Fault><detail><UPnPError xmlns="urn:schemas-upnp-org:control-1-0">
                <errorDescription>no code here</errorDescription>
              </UPnPError></detail></s:Fault></s:Body>
            </s:Envelope>
            """;

        var ok = SoapFaultParser.TryParse(Bytes(xml), out _);

        ok.Should().BeFalse();
    }

    [Fact]
    [Trait("ac", "AC-3.1.3")]
    public void TryParse_ZeroErrorCode_ReturnsFalse()
    {
        const string xml = """
            <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">
              <s:Body><s:Fault><detail><UPnPError xmlns="urn:schemas-upnp-org:control-1-0">
                <errorCode>0</errorCode>
              </UPnPError></detail></s:Fault></s:Body>
            </s:Envelope>
            """;

        SoapFaultParser.TryParse(Bytes(xml), out _).Should().BeFalse();
    }

    [Fact]
    [Trait("ac", "AC-3.1.3")]
    public void TryParse_RawFaultString_ReturnsFalse()
    {
        // A raw HTML/text 500 body with no UPnPError structure.
        SoapFaultParser.TryParse(Bytes("<html><body>500 Internal Server Error</body></html>"), out _)
            .Should().BeFalse();
    }

    [Fact]
    [Trait("ac", "AC-3.1.3")]
    public void TryParse_MalformedXml_ReturnsFalse()
    {
        SoapFaultParser.TryParse(Bytes("<s:Envelope><s:Body><unclosed>"), out _)
            .Should().BeFalse();
    }

    [Fact]
    [Trait("ac", "AC-3.1.3")]
    public void TryParse_XxeAttempt_ReturnsFalse()
    {
        // DOCTYPE/ENTITY is rejected by the shared XXE-locked settings (DtdProcessing.Prohibit)
        // → XmlException → caught → false. The entity never resolves to the filesystem.
        const string xxe = """
            <?xml version="1.0"?>
            <!DOCTYPE foo [ <!ENTITY xxe SYSTEM "file:///etc/passwd"> ]>
            <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">
              <s:Body><s:Fault><detail><UPnPError xmlns="urn:schemas-upnp-org:control-1-0">
                <errorCode>402</errorCode>
                <errorDescription>&xxe;</errorDescription>
              </UPnPError></detail></s:Fault></s:Body>
            </s:Envelope>
            """;

        SoapFaultParser.TryParse(Bytes(xxe), out _).Should().BeFalse();
    }
}
