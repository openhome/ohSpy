namespace ohSpy.Core.Diagnostics;

/// <summary>
/// No-op <see cref="IDiagnosticEmitter"/> placeholder. Story 1.5 replaces the DI
/// registration with the real <c>DiagnosticEmitter</c> + ring/file sinks. Marked
/// <c>internal</c> because nothing outside DI should ever reference it.
/// </summary>
internal sealed class NoOpDiagnosticEmitter : IDiagnosticEmitter
{
    public void Verbose(string category, string message, DiagnosticContext context = default) { }
    public void Information(string category, string message, DiagnosticContext context = default) { }
    public void Warning(string category, string message, DiagnosticContext context = default) { }
    public void Error(string category, string message, DiagnosticContext context = default) { }
}
