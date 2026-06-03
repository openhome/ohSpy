namespace ohSpy.Core.Models;

/// <summary>
/// A structured SOAP action invocation response. On HTTP 200, <c>InvokeActionAsync</c>
/// lifts each <c>&lt;argName&gt;value&lt;/argName&gt;</c> out of the
/// <c>&lt;u:ActionNameResponse&gt;</c> element (via <c>SoapResponseReader</c>) into
/// <see cref="OutputArguments"/>. Fault (HTTP 500) and transport errors are surfaced as
/// typed exceptions, never as a response — so there is no status/raw-XML carried here.
/// </summary>
/// <param name="ActionName">The action that was invoked (echoed back for caller convenience).</param>
/// <param name="OutputArguments">Output arguments parsed from the response envelope, in document order.</param>
public sealed record SoapResponse(
    string ActionName,
    IReadOnlyList<SoapArgument> OutputArguments);
