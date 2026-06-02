namespace ohSpy.Core.Http;

/// <summary>
/// Pre-built SOAP request envelope ready for POST. Story 3.1 will introduce a builder
/// that constructs this from <c>ScpdAction</c> + argument values; for now it's
/// constructed manually by test code.
/// </summary>
/// <param name="ControlUrl">Absolute URL of the service's controlURL endpoint.</param>
/// <param name="ServiceType">UPnP serviceType URN, e.g. <c>urn:schemas-upnp-org:service:AVTransport:1</c>.</param>
/// <param name="ActionName">Action name as declared in SCPD.</param>
/// <param name="EnvelopeXml">Complete SOAP envelope XML, UTF-8 encoded.</param>
public sealed record SoapRequest(
    Uri ControlUrl,
    string ServiceType,
    string ActionName,
    string EnvelopeXml);
