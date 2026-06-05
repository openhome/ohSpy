namespace ohSpy.Core.Tests.Diagnostics;

using FluentAssertions;
using Microsoft.Extensions.Options;
using ohSpy.Core.Diagnostics;

/// <summary>
/// Story 5.1 (Q1) — the runtime-mutable emitter-severity gate. Seeded from
/// <see cref="DiagnosticOptions.MinSeverity"/>; mutated by the Diagnostics viewer at runtime.
/// </summary>
public class DiagnosticLevelGateTests
{
    private static DiagnosticLevelGate Make(DiagSeverity seed) =>
        new(Options.Create(new DiagnosticOptions { MinSeverity = seed }));

    [Theory]
    [Trait("ac", "AC-5.1.10")]
    [InlineData(DiagSeverity.Verbose)]
    [InlineData(DiagSeverity.Information)]
    [InlineData(DiagSeverity.Warning)]
    [InlineData(DiagSeverity.Error)]
    public void Ctor_SeedsMinSeverityFromOptions(DiagSeverity seed)
    {
        var gate = Make(seed);
        gate.MinSeverity.Should().Be(seed, "the gate's initial value is seeded from DiagnosticOptions.MinSeverity");
    }

    [Fact]
    [Trait("ac", "AC-5.1.10")]
    public void MinSeverity_IsRuntimeMutable()
    {
        var gate = Make(DiagSeverity.Information);

        gate.MinSeverity = DiagSeverity.Verbose;
        gate.MinSeverity.Should().Be(DiagSeverity.Verbose);

        gate.MinSeverity = DiagSeverity.Error;
        gate.MinSeverity.Should().Be(DiagSeverity.Error);
    }
}
