namespace ohSpy.Core.Tests.Diagnostics;

using System.Collections.Generic;
using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ohSpy.Core.Collections;
using ohSpy.Core.Diagnostics;

/// <summary>
/// AC-3: fan-out to all three sinks; allocation-elision below MinSeverity; severity → LogLevel
/// mapping; AC-8.7 zero-allocation guarantee; AC-8.8 100 µs per-call budget.
/// </summary>
public class DiagnosticEmitterTests
{
    // Story 5.1 (Q1): the emitter now gates on the runtime IDiagnosticLevelGate (seeded from
    // DiagnosticOptions.MinSeverity) instead of the init-only option. This helper builds a real gate
    // at the requested level so the existing fan-out / elision / mapping tests keep their intent.
    private static DiagnosticLevelGate Gate(DiagSeverity minSeverity = DiagSeverity.Verbose) =>
        new(Options.Create(new DiagnosticOptions { MinSeverity = minSeverity }));

    [Fact]
    [Trait("ac", "AC-3")]
    public void Warning_FansOutToAllThreeSinks()
    {
        var logger = new CapturingLogger<DiagnosticEmitter>();
        var ring = new RecordingRingSink();
        var file = new RecordingFileSink();
        var emitter = new DiagnosticEmitter(logger, ring, file, Gate());

        var ctx = new DiagnosticContext { Url = "http://test/" };
        emitter.Warning(DiagCategories.HttpTimeout, "test message", ctx);

        logger.Records.Should().HaveCount(1);
        logger.Records[0].Level.Should().Be(LogLevel.Warning);
        logger.Records[0].Message.Should().Contain(DiagCategories.HttpTimeout);
        logger.Records[0].Message.Should().Contain("test message");

        ring.Pushed.Should().HaveCount(1);
        ring.Pushed[0].Severity.Should().Be(DiagSeverity.Warning);
        ring.Pushed[0].Category.Should().Be(DiagCategories.HttpTimeout);
        ring.Pushed[0].Message.Should().Be("test message");
        ring.Pushed[0].Context.Should().Be(ctx);

        file.Pushed.Should().HaveCount(1);
        file.Pushed[0].Should().BeSameAs(ring.Pushed[0]);
    }

    [Fact]
    [Trait("ac", "AC-3")]
    public void Verbose_BelowMinSeverity_DoesNotEmit()
    {
        var logger = new CapturingLogger<DiagnosticEmitter>();
        var ring = new RecordingRingSink();
        var file = new RecordingFileSink();
        var emitter = new DiagnosticEmitter(logger, ring, file, Gate(DiagSeverity.Information));

        emitter.Verbose(DiagCategories.GenaNotifyReceived, "should be silenced");

        logger.Records.Should().BeEmpty();
        ring.Pushed.Should().BeEmpty();
        file.Pushed.Should().BeEmpty();
    }

    [Fact]
    [Trait("ac", "AC-8.7")]
    public void Verbose_BelowMinSeverity_AllocatesZeroDiagnosticEntries()
    {
        var logger = new CapturingLogger<DiagnosticEmitter>();
        var ring = new RecordingRingSink();
        var file = new RecordingFileSink();
        var emitter = new DiagnosticEmitter(logger, ring, file, Gate(DiagSeverity.Information));

        // Pre-warm any one-time JIT / static init / tiered-compilation promotion in the
        // early-return path so the snapshot diff measures steady-state allocation only.
        for (int i = 0; i < 10_000; i++)
        {
            emitter.Verbose("warm", "warm");
        }

        // Force a clean GC baseline.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        const int iterations = 100_000;
        // Thread-local measurement (NOT GC.GetTotalAllocatedBytes, which is process-wide):
        // xUnit runs test classes in parallel, so a process-wide counter folds in
        // allocations from concurrently-running tests on other threads and makes this
        // assertion flaky. GetAllocatedBytesForCurrentThread isolates this loop's thread.
        var before = GC.GetAllocatedBytesForCurrentThread();

        for (int i = 0; i < iterations; i++)
        {
            // No allocations expected in the loop body itself — string literals are
            // interned, the default DiagnosticContext is a readonly struct, the cat/msg
            // args don't box.
            emitter.Verbose("category", "message");
        }

        var after = GC.GetAllocatedBytesForCurrentThread();
        var delta = after - before;
        var bytesPerCall = (double)delta / iterations;

        // AC-8.7 intent: zero per-call DiagnosticEntry allocation. A real DiagnosticEntry
        // (record + heap object header + 5 fields including a struct + 2 string refs) is
        // ~64+ bytes. We allow up to 4 bytes/call to absorb runtime-level overhead
        // (tiered-compilation promotion, GC bookkeeping in GetTotalAllocatedBytes itself,
        // etc.) — orders of magnitude below what a non-elided allocation would cost.
        bytesPerCall.Should().BeLessThan(4d,
            $"AC-8.7 requires zero DiagnosticEntry allocation in the elision path; observed {bytesPerCall:F3} bytes/call ({delta} bytes over {iterations} iterations)");
    }

    [Theory]
    [Trait("ac", "AC-3")]
    [InlineData(DiagSeverity.Verbose, LogLevel.Trace)]
    [InlineData(DiagSeverity.Information, LogLevel.Information)]
    [InlineData(DiagSeverity.Warning, LogLevel.Warning)]
    [InlineData(DiagSeverity.Error, LogLevel.Error)]
    public void Severity_MapsToCorrectLogLevel(DiagSeverity severity, LogLevel expected)
    {
        var logger = new CapturingLogger<DiagnosticEmitter>();
        var ring = new RecordingRingSink();
        var file = new RecordingFileSink();
        var emitter = new DiagnosticEmitter(logger, ring, file, Gate());

        switch (severity)
        {
            case DiagSeverity.Verbose: emitter.Verbose("c", "m"); break;
            case DiagSeverity.Information: emitter.Information("c", "m"); break;
            case DiagSeverity.Warning: emitter.Warning("c", "m"); break;
            case DiagSeverity.Error: emitter.Error("c", "m"); break;
        }

        logger.Records.Should().HaveCount(1);
        logger.Records[0].Level.Should().Be(expected);
    }

    [Fact]
    [Trait("ac", "AC-5.1.10")]
    public void RuntimeGateRaise_StopsBelowGateEntries()
    {
        // Story 5.1 (Q1): the emitter gates on the runtime gate, so RAISING the gate at runtime turns
        // the lower-severity firehose OFF — entries below the new level are no longer emitted.
        var logger = new CapturingLogger<DiagnosticEmitter>();
        var ring = new RecordingRingSink();
        var file = new RecordingFileSink();
        var gate = Gate(DiagSeverity.Verbose);
        var emitter = new DiagnosticEmitter(logger, ring, file, gate);

        emitter.Verbose("c", "before-raise"); // emitted (gate = Verbose)
        ring.Pushed.Should().HaveCount(1);

        gate.MinSeverity = DiagSeverity.Warning; // raise at runtime
        emitter.Verbose("c", "after-raise");     // dropped
        emitter.Information("c", "info");         // dropped

        ring.Pushed.Should().HaveCount(1, "raising the runtime gate must stop below-gate entries");
    }

    [Fact]
    [Trait("ac", "AC-5.1.10")]
    public void RuntimeGateLower_StartsBelowGateEntries()
    {
        // LOWERING the gate at runtime turns the firehose ON — Verbose entries start entering.
        var logger = new CapturingLogger<DiagnosticEmitter>();
        var ring = new RecordingRingSink();
        var file = new RecordingFileSink();
        var gate = Gate(DiagSeverity.Information);
        var emitter = new DiagnosticEmitter(logger, ring, file, gate);

        emitter.Verbose("c", "before-lower"); // dropped (gate = Information)
        ring.Pushed.Should().BeEmpty();

        gate.MinSeverity = DiagSeverity.Verbose; // lower at runtime
        emitter.Verbose("c", "after-lower");     // emitted

        ring.Pushed.Should().HaveCount(1, "lowering the runtime gate must start below-gate entries");
        ring.Pushed[0].Message.Should().Be("after-lower");
    }

    [Fact]
    [Trait("ac", "AC-8.7")]
    public void RuntimeGate_BelowGate_AllocatesZeroDiagnosticEntries()
    {
        // AC-8.7 preserved under the runtime gate: a below-gate severity still returns before any
        // DiagnosticEntry allocation, even after the gate was raised at runtime.
        var logger = new CapturingLogger<DiagnosticEmitter>();
        var ring = new RecordingRingSink();
        var file = new RecordingFileSink();
        var gate = Gate(DiagSeverity.Verbose);
        var emitter = new DiagnosticEmitter(logger, ring, file, gate);
        gate.MinSeverity = DiagSeverity.Warning; // raise so Verbose is below-gate

        for (int i = 0; i < 10_000; i++)
        {
            emitter.Verbose("warm", "warm");
        }
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        const int iterations = 100_000;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iterations; i++)
        {
            emitter.Verbose("category", "message");
        }
        var after = GC.GetAllocatedBytesForCurrentThread();
        var bytesPerCall = (double)(after - before) / iterations;

        bytesPerCall.Should().BeLessThan(4d,
            $"AC-8.7 zero-alloc must hold under the runtime gate; observed {bytesPerCall:F3} bytes/call");
    }

    [Fact]
    [Trait("ac", "AC-8.8")]
    public void EmitCall_ReturnsWithin100Microseconds()
    {
        var logger = new CapturingLogger<DiagnosticEmitter>();
        var ring = new RecordingRingSink();
        var file = new RecordingFileSink();
        var emitter = new DiagnosticEmitter(logger, ring, file, Gate());

        // Pre-warm.
        for (int i = 0; i < 1000; i++)
        {
            emitter.Warning(DiagCategories.HttpTimeout, "warm");
        }

        const int iterations = 1000;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            emitter.Warning(DiagCategories.HttpTimeout, "test");
        }
        sw.Stop();

        var avgMicroseconds = (sw.Elapsed.TotalMilliseconds * 1000d) / iterations;
        avgMicroseconds.Should().BeLessThan(100d,
            $"AC-8.8 requires emit < 100 µs avg; observed {avgMicroseconds:F2} µs over {iterations} calls");
    }

    // ─── Test doubles ──────────────────────────────────────────────────────

    private sealed class RecordingRingSink : IDiagnosticRingSink
    {
        public List<DiagnosticEntry> Pushed { get; } = new();
        public BoundedObservableCollection<DiagnosticRow> Entries { get; } = new(16);
        public void Push(DiagnosticEntry entry) => Pushed.Add(entry);
    }

    private sealed class RecordingFileSink : IDiagnosticFileSink
    {
        public List<DiagnosticEntry> Pushed { get; } = new();
        public void Push(DiagnosticEntry entry) => Pushed.Add(entry);
        public Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public sealed record Record(LogLevel Level, EventId EventId, string Message, Exception? Exception);

        public List<Record> Records { get; } = new();

        IDisposable? ILogger.BeginScope<TState>(TState state) => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Records.Add(new Record(logLevel, eventId, formatter(state, exception), exception));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
