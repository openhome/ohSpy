namespace ohSpy.Core.Tests.Soap;

using System.Text;
using FluentAssertions;
using ohSpy.Core.Soap;

public class SoapResponseReaderTests
{
    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    [Trait("ac", "AC-3.1.4")]
    public void ReadOutputArguments_MultipleArgs_ReturnsThemInOrder()
    {
        const string xml = """
            <?xml version="1.0"?>
            <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/" s:encodingStyle="http://schemas.xmlsoap.org/soap/encoding/">
              <s:Body>
                <u:BrowseResponse xmlns:u="urn:schemas-upnp-org:service:ContentDirectory:1">
                  <Result>some-didl</Result>
                  <NumberReturned>10</NumberReturned>
                  <TotalMatches>42</TotalMatches>
                </u:BrowseResponse>
              </s:Body>
            </s:Envelope>
            """;

        var args = SoapResponseReader.ReadOutputArguments(Bytes(xml));

        args.Should().HaveCount(3);
        args[0].Name.Should().Be("Result");
        args[0].Value.Should().Be("some-didl");
        args[1].Name.Should().Be("NumberReturned");
        args[1].Value.Should().Be("10");
        args[2].Name.Should().Be("TotalMatches");
        args[2].Value.Should().Be("42");
    }

    [Fact]
    [Trait("ac", "AC-3.1.4")]
    public void ReadOutputArguments_EscapedValue_IsUnescaped()
    {
        const string xml = """
            <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">
              <s:Body>
                <u:BrowseResponse xmlns:u="urn:schemas-upnp-org:service:ContentDirectory:1">
                  <Result>&lt;DIDL-Lite&gt;&amp;stuff&lt;/DIDL-Lite&gt;</Result>
                </u:BrowseResponse>
              </s:Body>
            </s:Envelope>
            """;

        var args = SoapResponseReader.ReadOutputArguments(Bytes(xml));

        args.Should().ContainSingle();
        args[0].Name.Should().Be("Result");
        args[0].Value.Should().Be("<DIDL-Lite>&stuff</DIDL-Lite>");
    }

    [Fact]
    [Trait("ac", "AC-3.1.4")]
    public void ReadOutputArguments_ArgumentlessResponse_ReturnsEmptyList()
    {
        // Self-closing response wrapper ⇒ no output args (FR-031 symmetric case).
        const string xml = """
            <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">
              <s:Body>
                <u:SetVolumeResponse xmlns:u="urn:schemas-upnp-org:service:RenderingControl:1" />
              </s:Body>
            </s:Envelope>
            """;

        SoapResponseReader.ReadOutputArguments(Bytes(xml)).Should().BeEmpty();
    }

    [Fact]
    [Trait("ac", "AC-3.1.4")]
    public void ReadOutputArguments_EmptyResponseElement_ReturnsEmptyList()
    {
        // Non-self-closing but childless response wrapper.
        const string xml = """
            <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">
              <s:Body>
                <u:SetVolumeResponse xmlns:u="urn:schemas-upnp-org:service:RenderingControl:1"></u:SetVolumeResponse>
              </s:Body>
            </s:Envelope>
            """;

        SoapResponseReader.ReadOutputArguments(Bytes(xml)).Should().BeEmpty();
    }
}
