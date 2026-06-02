namespace ohSpy.Core.Tests.Fakes;

using ohSpy.Core.Diagnostics;

/// <summary>
/// Captures every emitter call into <see cref="Entries"/> so tests can assert the
/// HTTP-error diagnostic stream. Substitutes for <c>NoOpDiagnosticEmitter</c> in
/// tests that need to verify AC-5's "Warning diagnostic emitted on timeout" clause.
/// </summary>
internal sealed class CapturingDiagnosticEmitter : IDiagnosticEmitter
{
    public sealed record Entry(string Severity, string Category, string Message, DiagnosticContext Context);

    public List<Entry> Entries { get; } = new();

    public void Verbose(string c, string m, DiagnosticContext ctx = default) => Entries.Add(new("Verbose", c, m, ctx));
    public void Information(string c, string m, DiagnosticContext ctx = default) => Entries.Add(new("Information", c, m, ctx));
    public void Warning(string c, string m, DiagnosticContext ctx = default) => Entries.Add(new("Warning", c, m, ctx));
    public void Error(string c, string m, DiagnosticContext ctx = default) => Entries.Add(new("Error", c, m, ctx));
}
