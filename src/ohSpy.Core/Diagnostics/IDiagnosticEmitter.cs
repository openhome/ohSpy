namespace ohSpy.Core.Diagnostics;

/// <summary>
/// Fan-out emitter for structured diagnostic entries. Story 1.5 implements the
/// production sinks (ring + rolling file); Story 1.3 ships only an interface +
/// no-op impl so <c>UpnpHttpClient</c> can take this dependency.
/// </summary>
public interface IDiagnosticEmitter
{
    void Verbose(string category, string message, DiagnosticContext context = default);
    void Information(string category, string message, DiagnosticContext context = default);
    void Warning(string category, string message, DiagnosticContext context = default);

    // CA1716: "Error" matches a VB.NET keyword. We accept this — the diagnostic
    // severity naming (Verbose/Information/Warning/Error) mirrors Microsoft.Extensions.Logging
    // LogLevel and is the idiomatic .NET vocabulary. VB consumers would need bracket escaping;
    // ohSpy is C#-only so the trade-off is one-sided.
#pragma warning disable CA1716
    void Error(string category, string message, DiagnosticContext context = default);
#pragma warning restore CA1716
}
