namespace ohSpy.Core.Diagnostics;

/// <summary>
/// A single diagnostic entry emitted by <see cref="IDiagnosticEmitter"/>.
/// Carried through the emitter's fan-out to MEL <c>ILogger</c> + ring sink + file sink.
/// </summary>
/// <param name="TimestampUtc">UTC timestamp captured at emit time. Always UTC (never local).</param>
/// <param name="Severity">Diagnostic severity. Entries below <see cref="DiagnosticOptions.MinSeverity"/> are not emitted.</param>
/// <param name="Category">Category string — MUST be one of the <see cref="DiagCategories"/> constants.</param>
/// <param name="Message">Human-readable message; structured data belongs in <paramref name="Context"/>.</param>
/// <param name="Context">Structured context — mandatory fields per category documented on the <see cref="DiagCategories"/> constant.</param>
public sealed record DiagnosticEntry(
    DateTime TimestampUtc,
    DiagSeverity Severity,
    string Category,
    string Message,
    DiagnosticContext Context);
