namespace ohSpy.Core.Soap;

/// <summary>
/// Data carrier for a parsed UPnP fault (UDA 1.0 §3.2.2 — the
/// <c>&lt;UPnPError&gt;&lt;errorCode/&gt;&lt;errorDescription/&gt;&lt;/UPnPError&gt;</c>
/// inside a SOAP <c>&lt;s:Fault&gt;&lt;detail&gt;</c>). Deliberately distinct from
/// <c>UpnpFaultException</c>: the parser returns this pure data record; the HTTP client
/// decides whether to throw. Keeps the parser exception-free and unit-testable.
/// </summary>
/// <param name="ErrorCode">UPnP error code (a non-zero int gates a successful parse).</param>
/// <param name="ErrorDescription">Human-readable error description; <c>""</c> if the element was absent.</param>
public sealed record UpnpFault(int ErrorCode, string ErrorDescription);
