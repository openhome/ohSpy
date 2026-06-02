namespace ohSpy.Core.Http;

/// <summary>
/// Abstract base for UPnP-domain exceptions. Never thrown directly; consumers catch
/// either <see cref="UpnpException"/> for "any UPnP problem" or one of the four
/// sealed derivatives for type-specific handling.
/// </summary>
public abstract class UpnpException : Exception
{
    protected UpnpException(string message) : base(message) { }
    protected UpnpException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Thrown when a per-operation timeout budget elapses before the request completes.
/// Carries the originating URL plus the budget and actual elapsed time for diagnostics.
/// </summary>
public sealed class UpnpTimeoutException : UpnpException
{
    public Uri Url { get; }
    public TimeSpan Budget { get; }
    public TimeSpan Elapsed { get; }

    public UpnpTimeoutException(Uri url, TimeSpan budget, TimeSpan elapsed)
        : base($"UPnP request to {url} timed out after {elapsed.TotalMilliseconds:F0}ms (budget {budget.TotalMilliseconds:F0}ms)")
    {
        Url = url; Budget = budget; Elapsed = elapsed;
    }
}

/// <summary>
/// Thrown on transport-layer failure (HttpRequestException, socket error, DNS, etc.).
/// Carries the originating URL and the HTTP status code if one was received.
/// </summary>
public sealed class UpnpTransportException : UpnpException
{
    public Uri Url { get; }
    public int? StatusCode { get; }

    public UpnpTransportException(Uri url, string message, int? statusCode = null, Exception? inner = null)
        : base(message, inner ?? new InvalidOperationException(message))
    {
        Url = url; StatusCode = statusCode;
    }
}

/// <summary>
/// Thrown when the response violates UPnP protocol expectations: oversize body,
/// malformed framing, missing required header, etc.
/// </summary>
public sealed class UpnpProtocolException : UpnpException
{
    public Uri Url { get; }
    public UpnpProtocolException(Uri url, string message) : base(message) { Url = url; }
}

/// <summary>
/// Thrown when a SOAP action invocation returns a structured UPnP fault (HTTP 500 +
/// <c>&lt;s:Fault&gt;</c> body). Carries the action name plus the UPnP error code
/// and description from the fault detail.
/// </summary>
public sealed class UpnpFaultException : UpnpException
{
    public Uri Url { get; }
    public string ActionName { get; }
    public int ErrorCode { get; }
    public string ErrorDescription { get; }

    public UpnpFaultException(Uri url, string actionName, int errorCode, string errorDescription)
        : base($"UPnP fault from {url} action '{actionName}': {errorCode} {errorDescription}")
    {
        Url = url; ActionName = actionName; ErrorCode = errorCode; ErrorDescription = errorDescription;
    }
}
