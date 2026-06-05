namespace ohSpy.Core.Diagnostics;

/// <summary>
/// Runtime-mutable, thread-safe gate for the diagnostic emitter's minimum severity (Story 5.1, Q1).
/// <para>
/// The shipped <see cref="DiagnosticOptions.MinSeverity"/> is <c>{ get; init; }</c> — fixed at
/// composition time, so it cannot serve the operator's runtime "turn the Verbose firehose on/off"
/// control (Architecture D8). This seam holds the CURRENT minimum severity and is mutated by the
/// Diagnostics viewer's <c>MinSeverity</c> setter; <see cref="DiagnosticEmitter"/> reads it on every
/// emit (the AC-8.7 zero-allocation fast-path) INSTEAD of the init-only option.
/// </para>
/// <para>
/// The implementation MUST keep the read cheap (a single <c>Volatile.Read</c> of an <c>int</c>) — it
/// is on the emit hot path, hit by many threads. The gate's initial value is seeded FROM
/// <see cref="DiagnosticOptions.MinSeverity"/> so the configured startup default is preserved.
/// Not persisted across restart (PRD §7 Non-Goal).
/// </para>
/// </summary>
public interface IDiagnosticLevelGate
{
    /// <summary>
    /// The current minimum severity. Entries below this are never emitted (AC-8.7). Reads and writes
    /// are thread-safe (lock-free <c>Volatile.Read/Write</c> over an <c>int</c> backing field).
    /// </summary>
    DiagSeverity MinSeverity { get; set; }
}
