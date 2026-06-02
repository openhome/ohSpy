namespace ohSpy.Core.Diagnostics;

/// <summary>
/// Configuration for <see cref="DiagnosticEmitter"/>. Bound via
/// <c>services.Configure&lt;DiagnosticOptions&gt;(...)</c> (Pattern 7); resolved via
/// <see cref="Microsoft.Extensions.Options.IOptions{T}"/> at the emitter's ctor.
/// </summary>
public sealed class DiagnosticOptions
{
    /// <summary>
    /// Minimum severity to emit. Entries below this threshold are dropped WITHOUT
    /// constructing a <see cref="DiagnosticEntry"/> (AC-8.7). Default: <see cref="DiagSeverity.Information"/>.
    /// </summary>
    public DiagSeverity MinSeverity { get; init; } = DiagSeverity.Information;
}
