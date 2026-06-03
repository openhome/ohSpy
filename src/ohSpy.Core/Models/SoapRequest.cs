namespace ohSpy.Core.Models;

/// <summary>
/// A structured SOAP action invocation request. The invocation popup (Story 3.2) builds
/// one of these from an <c>ScpdAction</c> plus the operator's argument values; the HTTP
/// client (<c>InvokeActionAsync</c>) hands it to <c>SoapEnvelopeBuilder</c> to produce the
/// wire envelope. No pre-built XML string is carried here — envelope construction is the
/// builder's concern (single point of escaping + framing).
/// </summary>
/// <param name="ControlUrl">Absolute URL of the service's controlURL endpoint.</param>
/// <param name="ServiceType">UPnP serviceType URN, e.g. <c>urn:schemas-upnp-org:service:AVTransport:1</c>.</param>
/// <param name="ActionName">Action name as declared in SCPD.</param>
/// <param name="InputArguments">Input arguments, in the order they must appear in the envelope.</param>
public sealed record SoapRequest(
    Uri ControlUrl,
    string ServiceType,
    string ActionName,
    IReadOnlyList<SoapArgument> InputArguments);
