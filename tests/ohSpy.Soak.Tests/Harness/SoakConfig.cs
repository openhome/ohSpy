namespace ohSpy.Soak.Tests.Harness;

using System.Globalization;

/// <summary>
/// Story 6.2 (⭐#2 time-parameterisation) — resolves the soak run duration from an environment
/// variable, defaulting to a ~10-second STRUCTURAL SMOKE when unset so the same session script proves
/// it wires up, pumps, asserts, and writes a report in seconds (no multi-hour wait during dev).
/// <list type="bullet">
///   <item><c>OHSPY_SOAK_30MIN_DURATION</c> — the 30-min no-crash run (gate: <c>00:30:00</c>).</item>
///   <item><c>OHSPY_SOAK_8HR_DURATION</c> — the 8-hour scale-ceiling run (gate: <c>08:00:00</c>).</item>
///   <item><c>OHSPY_SOAK_DURATION</c> — a global override applied to BOTH if the specific var is unset.</item>
/// </list>
/// Memory-sample COUNT and other internal cadences scale to the run length so the smoke and the gate
/// run the same logic, compressed.
/// </summary>
internal static class SoakConfig
{
    /// <summary>Default structural-smoke duration (the harness proof, not the gate). ~10 s.</summary>
    public static readonly TimeSpan SmokeDefault = TimeSpan.FromSeconds(10);

    public static TimeSpan ThirtyMinuteDuration() =>
        Resolve("OHSPY_SOAK_30MIN_DURATION") ?? Resolve("OHSPY_SOAK_DURATION") ?? SmokeDefault;

    public static TimeSpan EightHourDuration() =>
        Resolve("OHSPY_SOAK_8HR_DURATION") ?? Resolve("OHSPY_SOAK_DURATION") ?? SmokeDefault;

    /// <summary>True when running the compressed structural smoke (no duration override set).</summary>
    public static bool IsSmoke(TimeSpan duration) => duration <= SmokeDefault;

    private static TimeSpan? Resolve(string envVar)
    {
        var raw = Environment.GetEnvironmentVariable(envVar);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }
        if (TimeSpan.TryParse(raw, CultureInfo.InvariantCulture, out var ts) && ts > TimeSpan.Zero)
        {
            return ts;
        }
        throw new FormatException(
            $"{envVar}='{raw}' is not a valid positive TimeSpan (expected e.g. 00:30:00 or 08:00:00).");
    }
}
