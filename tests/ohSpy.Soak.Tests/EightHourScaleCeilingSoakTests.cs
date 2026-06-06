namespace ohSpy.Soak.Tests;

// ⚠️ SOAK FLAKE DISCIPLINE (Story 6.2): [Trait("category","soak")] — a soak flake is a REAL DEFECT,
// investigated and fixed, NEVER retried-until-green.
//
// ⚠️ DURATION: time-parameterised via OHSPY_SOAK_8HR_DURATION (default ~10 s structural smoke). The
// 8-hour full run is OPTIONAL (not a required release gate per the Project Lead) — the required evidence
// is the 30-min soak + this structural quick-validation + Story 6.3's interactive 1-hour SC-013. This
// test stays present + runnable at `OHSPY_SOAK_8HR_DURATION=08:00:00`. See docs/DEVELOPMENT.md.
//
// ⚠️ NOT IN ohSpy.sln — invoked BY PATH ONLY.

using FluentAssertions;
using ohSpy.Soak.Tests.Harness;

/// <summary>
/// 8-hour scale-ceiling soak (Scale ceiling / SC-013). 20 announcing devices + 5 subscription popups
/// receiving moderate NOTIFY traffic with the SSDP log held at saturation (≥ 1 adv/s), asserting
/// resident memory stays bounded (no leak) and &lt; 200 MB HEADLESS, the bounded collections behave at
/// their SHIPPED caps, and the on-disk log rolls over.
/// <para>⭐#4: the &lt; 200 MB assertion is a HEADLESS Core figure (test host + Kestrel farm, NOT the full
/// WinUI app). The full-app RSS is verified by Story 6.3's SC-013. Stated honestly in the report.</para>
/// </summary>
[Trait("category", "soak")]
public sealed class EightHourScaleCeilingSoakTests
{
    private const long HeadlessMemoryCeilingBytes = 200L * 1024 * 1024; // generous headless ceiling (⭐#4)

    [Fact]
    public async Task EightHour_ScaleCeiling_MemoryBoundedCapsHeldRolloverApplied()
    {
        var duration = SoakConfig.EightHourDuration();
        var startUtc = DateTime.UtcNow;
        const int scaleDevices = 20;       // AC-6.2.6
        const int subscriptionPopups = 5;  // AC-6.2.6: 5 subscription popups

        // ≥ 1 adv/s sustained keeps the 10,000-cap SSDP log at/near saturation; use a brisk rate so the
        // compressed smoke still drives the log toward its cap.
        await using var harness = new SoakHarness(advertsPerSecond: 40);
        var anomalies = new List<string>();

        await harness.StartAsync(scaleDevices, includeMisbehaving: true, CancellationToken.None);

        var sampleCount = SoakConfig.IsSmoke(duration) ? 6 : (int)Math.Max(6, duration.TotalMinutes / 10);
        var runner = new SoakRunner(harness, duration, sampleCount);
        await runner.RunAsync(subscriptionPopups, CancellationToken.None);

        // ── Memory (AC-6.2.7) ──
        var memory = runner.Memory;
        var maxWorkingSet = memory.MaxWorkingSetBytes;
        var bounded = memory.IsBounded();

        // ── Bounded caps against the SHIPPED constants (AC-6.2.8 / ⭐#3) ──
        await harness.FlushDiagnosticsAsync();
        var caps = ThirtyMinuteNoCrashSoakTests.SnapshotCaps(harness, runner);
        var disk = OnDiskLogInspector.Inspect(harness.DiagnosticsTempDir);

        // ── Popups closable + diagnostics responsive at session end (AC-6.2.5 parity with the 30-min
        //    soak). These MUST be derived + asserted, never hardcoded — a popup that hangs/throws on
        //    Dispose, or a diagnostics VM that lost the live ring, has to FAIL the 8-hour run. ──
        var diagnosticsResponsive = ReferenceEquals(harness.Diagnostics.Entries, harness.RingSink.Entries);
        harness.Diagnostics.MinSeverity = ohSpy.Core.Diagnostics.DiagSeverity.Warning;
        diagnosticsResponsive = diagnosticsResponsive &&
            harness.Diagnostics.MinSeverity == ohSpy.Core.Diagnostics.DiagSeverity.Warning;
        harness.CloseAllPopups();

        UnhandledExceptionCapture.FlushFinalizers();
        await harness.Ui.DrainAsync();

        var popupsClosable = !harness.Exceptions.Any;

        if (!bounded) anomalies.Add("private memory did not plateau after warm-up (possible leak).");
        if (maxWorkingSet >= HeadlessMemoryCeilingBytes)
            anomalies.Add($"headless WorkingSet64 peaked at {maxWorkingSet / (1024 * 1024)} MB (≥ 200 MB).");
        if (!diagnosticsResponsive) anomalies.Add("diagnostics viewer not responsive at session end (ring instance changed or gate setter stuck).");
        if (!popupsClosable) anomalies.Add("a subscription popup failed to close cleanly.");
        if (harness.Exceptions.Any) anomalies.Add($"{harness.Exceptions.Captured.Count} unhandled exception(s).");

        ThirtyMinuteNoCrashSoakTests.WriteReport("8hr", startUtc, duration, runner, harness,
            scaleDevices, subscriptionPopups, disk, caps,
            popupsClosable, diagnosticsResponsive, anomalies);

        // ── Hard gate asserts ──
        harness.Exceptions.Captured.Should().BeEmpty("0 unhandled exceptions over the run (AC-6.2.9)");
        popupsClosable.Should().BeTrue("every opened subscription popup disposes cleanly (closable, AC-6.2.5)");
        diagnosticsResponsive.Should().BeTrue(
            "DiagnosticsViewModel responsive at session end — binds the live ring + gate setter round-trips (AC-6.2.5)");

        // Bounded collections behave (asserted against the shipped caps, not retyped literals).
        caps.SsdpLogCount.Should().BeLessThanOrEqualTo(caps.SsdpLogCap,
            "SSDP log stays at/under its shipped 10,000 cap after saturation (AC-6.2.8)");
        caps.MaxEventListCount.Should().BeLessThanOrEqualTo(caps.EventListCap,
            "each subscription event list stays at/under its shipped 5,000 cap (AC-6.2.8)");
        caps.RingCount.Should().BeLessThanOrEqualTo(caps.RingCap,
            "the diagnostic ring stays at/under its shipped 5,000 cap (AC-6.2.8)");
        disk.TotalBytes.Should().BeLessThanOrEqualTo(
            ShippedCaps.DiagnosticFileTotalCapBytes + ShippedCaps.DiagnosticFileMaxBytes,
            "on-disk log stays at/under ≤ 16 MB (≤ 8 × 2 MB) plus a single-entry slop (AC-6.2.8 / ⭐#6)");
        disk.FileCount.Should().BeLessThanOrEqualTo(ShippedCaps.DiagnosticFileMaxRetained,
            "on-disk retention holds at ≤ 8 files (AC-6.2.8)");
        if (!SoakConfig.IsSmoke(duration))
            disk.FileCount.Should().BeGreaterThanOrEqualTo(2,
                "on-disk rollover MUST actually apply in a real gate run (AC-6.2.11) — a run that never crossed the 2 MB/file cap would otherwise pass silently");

        // Memory: bounded/no-leak + < 200 MB HEADLESS (⭐#4 — full-app RSS is 6.3's SC-013).
        memory.Samples.Count.Should().BeGreaterThanOrEqualTo(3,
            "the no-leak heuristic needs ≥ 3 samples to judge a trend (guards against a vacuous IsBounded pass)");
        bounded.Should().BeTrue("resident memory is bounded / shows no upward leak trend after warm-up (AC-6.2.7)");
        maxWorkingSet.Should().BeLessThan(HeadlessMemoryCeilingBytes,
            "headless WorkingSet64 stays under the generous 200 MB ceiling at every sample (AC-6.2.7; HEADLESS — full-app is 6.3)");
    }
}
