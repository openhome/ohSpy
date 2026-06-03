namespace ohSpy.Core.ViewModels;

using ohSpy.Core.Models;

/// <summary>
/// The outcome of a SOAP action invocation, rendered in the popup's result area (Story 3.2).
/// An abstract base with three sealed variants (Pattern 9 sealed-record data carriers) — the
/// App projects each runtime type to its own result template / Visibility (mirror
/// <c>PropertiesWindow</c>'s code-behind projections; Visibility stays out of Core per Pattern 2).
/// Output pairs reuse <see cref="SoapArgument"/> — the response already returns
/// <c>IReadOnlyList&lt;SoapArgument&gt;</c>, so no new pair type is invented.
/// </summary>
public abstract record InvocationResultViewModel;

/// <summary>
/// Success (FR-028): the device returned HTTP 200 with zero or more output arguments. An
/// argument-less response carries an empty <see cref="Outputs"/> list — the App renders a
/// neutral "Success (no output)" message (FR-031).
/// </summary>
public sealed record SuccessResult(IReadOnlyList<SoapArgument> Outputs) : InvocationResultViewModel;

/// <summary>
/// UPnP fault (FR-029): the device returned HTTP 500 with a structured <c>&lt;s:Fault&gt;</c>
/// body. Carries the HTTP status (always 500 here), the UPnP error code, and its description.
/// </summary>
public sealed record FaultResult(int StatusCode, int ErrorCode, string ErrorDescription)
    : InvocationResultViewModel;

/// <summary>
/// Transport error (FR-030): timeout, connection failure, malformed response, or an
/// unresolvable control URL — anything that is not a structured UPnP fault. Carries a
/// human-readable message (URL + status-if-known + the exception text).
/// </summary>
public sealed record TransportErrorResult(string Message) : InvocationResultViewModel;
