namespace ohSpy.Core.Models;

/// <summary>
/// A single SOAP action argument — a name/value pair. Used for both input arguments
/// (carried into <see cref="SoapRequest"/>) and output arguments (returned in
/// <see cref="SoapResponse"/>). Values are free-form text: per PRD §7 Non-Goal, v1 does
/// NOT do <c>&lt;dataType&gt;</c>-driven typed inputs — the operator types the literal
/// string and the device validates.
/// </summary>
/// <param name="Name">Argument name as declared in the service's SCPD.</param>
/// <param name="Value">Argument value as free-form text (XML-escaped only at envelope-build time).</param>
public sealed record SoapArgument(string Name, string Value);
