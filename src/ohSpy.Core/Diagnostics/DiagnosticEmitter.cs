namespace ohSpy.Core.Diagnostics;

using Microsoft.Extensions.Logging;

internal sealed class DiagnosticEmitter : IDiagnosticEmitter
{
    private readonly ILogger<DiagnosticEmitter> _logger;
    private readonly IDiagnosticRingSink _ring;
    private readonly IDiagnosticFileSink _file;
    private readonly IDiagnosticLevelGate _gate;

    public DiagnosticEmitter(
        ILogger<DiagnosticEmitter> logger,
        IDiagnosticRingSink ring,
        IDiagnosticFileSink file,
        IDiagnosticLevelGate gate)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(ring);
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(gate);
        _logger = logger;
        _ring = ring;
        _file = file;
        _gate = gate;
    }

    public void Verbose(string category, string message, DiagnosticContext context = default)
        => Emit(DiagSeverity.Verbose, category, message, context);

    public void Information(string category, string message, DiagnosticContext context = default)
        => Emit(DiagSeverity.Information, category, message, context);

    public void Warning(string category, string message, DiagnosticContext context = default)
        => Emit(DiagSeverity.Warning, category, message, context);

    public void Error(string category, string message, DiagnosticContext context = default)
        => Emit(DiagSeverity.Error, category, message, context);

    private void Emit(DiagSeverity severity, string category, string message, DiagnosticContext context)
    {
        // AC-8.7: allocation-elision. The threshold check happens BEFORE constructing the
        // DiagnosticEntry record, capturing DateTime.UtcNow, or building the EventId. If
        // below the gate's MinSeverity, return immediately — zero DiagnosticEntry allocation,
        // zero downstream work. Story 5.1 (Q1): the gate is a runtime-mutable seam
        // (IDiagnosticLevelGate, a single Volatile.Read of an int) so the Diagnostics viewer
        // can flip the Verbose firehose on/off at runtime — the shipped DiagnosticOptions
        // .MinSeverity is init-only and cannot serve that. The gate was SEEDED from
        // DiagnosticOptions.MinSeverity at startup, so the configured default still holds.
        if (severity < _gate.MinSeverity)
        {
            return;
        }

        var entry = new DiagnosticEntry(DateTime.UtcNow, severity, category, message, context);

        // Fan-out: all three sinks receive the same entry. None of them block.
        //   - MEL ILogger: synchronous, but goes to .NET observability pipeline (dotnet-trace etc.)
        //   - Ring sink: dispatcher-posted prepend (non-blocking)
        //   - File sink: channel-enqueue (non-blocking)
        //
        // CA1848 suppression: source-generated LoggerMessage delegates would be the
        // micro-optimal pattern, but the emitter is THE single chokepoint for the entire
        // app's diagnostic fan-out — adding generated partial classes per (severity, category)
        // pair is more ceremony than the perf win warrants here. We've already done the most
        // valuable optimisation: the MinSeverity allocation-elision check above (AC-8.7).
        // CA1873 suppression: args here are cheap (two strings + an ordinal hash int). The
        // analyzer can't see that we've already gated severity above; below MinSeverity we
        // return immediately without evaluating anything.
#pragma warning disable CA1848, CA1873
        _logger.Log(
            MapSeverity(severity),
            new EventId(category.GetHashCode(StringComparison.Ordinal), category),
            "[{Category}] {Message}",
            category,
            message);
#pragma warning restore CA1848, CA1873
        _ring.Push(entry);
        _file.Push(entry);
    }

    private static LogLevel MapSeverity(DiagSeverity s) => s switch
    {
        DiagSeverity.Verbose => LogLevel.Trace,
        DiagSeverity.Information => LogLevel.Information,
        DiagSeverity.Warning => LogLevel.Warning,
        DiagSeverity.Error => LogLevel.Error,
        _ => LogLevel.None,
    };
}
