namespace ohSpy.Core.Http;

using System.Net;

/// <summary>
/// Raw SOAP response. Story 3.1 will introduce a parser that lifts output args out of
/// <see cref="ResponseXml"/>.
/// </summary>
/// <param name="StatusCode">HTTP status of the response (typically 200 OK; 500 only when a SOAP fault was raised and converted to <see cref="UpnpFaultException"/>).</param>
/// <param name="ResponseXml">Complete response envelope as a UTF-8 string.</param>
public sealed record SoapResponse(HttpStatusCode StatusCode, string ResponseXml);
