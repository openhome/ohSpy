namespace ohSpy.Soak.Tests.Harness;

using System.Diagnostics;

/// <summary>
/// Story 6.2 (AC-6.2.7) — samples process memory (<see cref="Process.WorkingSet64"/> +
/// <see cref="Process.PrivateMemorySize64"/>) at run-relative timestamps. Real gate cadence is "every
/// 10 min"; for the compressed smoke the caller takes a fixed number of samples across the run so the
/// SAME no-leak / bounded analysis applies at any duration.
/// <para>
/// ⭐#4 caveat: this is the HEADLESS Core soak process (test host + Kestrel farm), NOT the full WinUI
/// app — the &lt; 200 MB ceiling is a generous headless bound; the full-app RSS is verified by 6.3.
/// </para>
/// </summary>
internal sealed class MemorySampler
{
    public readonly record struct Sample(TimeSpan At, long WorkingSetBytes, long PrivateBytes);

    private readonly List<Sample> _samples = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public IReadOnlyList<Sample> Samples => _samples;

    public Sample Take()
    {
        // Refresh() so cached values are current.
        using var proc = Process.GetCurrentProcess();
        var sample = new Sample(_clock.Elapsed, proc.WorkingSet64, proc.PrivateMemorySize64);
        _samples.Add(sample);
        return sample;
    }

    public long MaxWorkingSetBytes => _samples.Count == 0 ? 0 : _samples.Max(s => s.WorkingSetBytes);

    /// <summary>
    /// No-leak heuristic (AC-6.2.7 "bounded / no upward leak trend after warm-up"): after dropping the
    /// first sample (warm-up), the LAST private-memory sample must not exceed the post-warm-up MINIMUM
    /// by more than <paramref name="allowedGrowthFactor"/>. A genuine leak trends monotonically up well
    /// beyond a steady-state plateau; transient GC wobble stays within the factor.
    /// </summary>
    public bool IsBounded(double allowedGrowthFactor = 2.0)
    {
        if (_samples.Count < 2)
        {
            return true; // <2 samples genuinely cannot show a trend (the tests also assert Samples.Count >= 3)
        }
        var postWarmup = _samples.Skip(1).Select(s => s.PrivateBytes).ToArray();
        var min = postWarmup.Min();
        var last = postWarmup[^1];
        if (min <= 0)
        {
            return true;
        }
        return last <= min * allowedGrowthFactor;
    }
}
