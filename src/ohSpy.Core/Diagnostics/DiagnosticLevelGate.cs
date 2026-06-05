namespace ohSpy.Core.Diagnostics;

using System.Threading;
using Microsoft.Extensions.Options;

/// <summary>
/// Lock-free <see cref="IDiagnosticLevelGate"/>. The current minimum severity is held as an
/// <c>int</c> (the <see cref="DiagSeverity"/> ordinal) and accessed via <see cref="Volatile.Read"/> /
/// <see cref="Volatile.Write"/> — an <c>enum</c> cannot be a <c>volatile</c> field directly, but an
/// <c>int</c> can be read/written without tearing on every supported platform, which is sufficient
/// for a single-value gate (no compound state, so no lock needed).
/// <para>
/// Seeded from <see cref="DiagnosticOptions.MinSeverity"/> at construction so the configured startup
/// default is preserved; the Diagnostics viewer then mutates it at runtime.
/// </para>
/// </summary>
internal sealed class DiagnosticLevelGate : IDiagnosticLevelGate
{
    // Backing store for the DiagSeverity ordinal. Accessed only via Volatile.Read/Write.
    private int _minSeverity;

    public DiagnosticLevelGate(IOptions<DiagnosticOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        // Seed from the configured startup default (DiagnosticOptions.MinSeverity is init-only).
        Volatile.Write(ref _minSeverity, (int)options.Value.MinSeverity);
    }

    public DiagSeverity MinSeverity
    {
        get => (DiagSeverity)Volatile.Read(ref _minSeverity);
        set => Volatile.Write(ref _minSeverity, (int)value);
    }
}
