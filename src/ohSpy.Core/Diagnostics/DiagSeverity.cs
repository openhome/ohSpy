namespace ohSpy.Core.Diagnostics;

/// <summary>
/// Severity of a diagnostic entry. Maps to MEL <c>LogLevel</c> as:
/// Verbose → Trace, Information → Information, Warning → Warning, Error → Error.
/// </summary>
public enum DiagSeverity
{
    Verbose,
    Information,
    Warning,
    Error,
}
