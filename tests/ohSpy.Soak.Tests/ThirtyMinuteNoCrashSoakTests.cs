namespace ohSpy.Soak.Tests;

// ⚠️ SOAK FLAKE DISCIPLINE (Story 6.2 / NFR-R1): every test here is [Trait("category","soak")] and a
// soak flake is a REAL DEFECT — investigated and fixed, NEVER retried-until-green. NFR-R1 and the Scale
// Ceiling are not statistical claims; a single failure is a defect to fix (with its own regression test).
//
// ⚠️ DURATION: time-parameterised via OHSPY_SOAK_30MIN_DURATION (default ~10 s structural smoke). The
// real release gate is `OHSPY_SOAK_30MIN_DURATION=00:30:00`. See docs/DEVELOPMENT.md.
//
// ⚠️ NOT IN ohSpy.sln — this project is invoked BY PATH ONLY (`dotnet test tests/ohSpy.Soak.Tests`), so a
// bare/solution-wide `dotnet test` never triggers it; the soak trait excludes it from the chaos hook too.

using FluentAssertions;
using ohSpy.Soak.Tests.Harness;

/// <summary>
/// 30-min no-crash soak (SC-R-30min / NFR-R1). A farm of 15 normal + 3+ misbehaving devices driven
/// through the representative debugging-session script for the configured duration, asserting 0
/// crashes / 0 UI-thread stalls &gt; 1 s / 0 unclosable popups / diagnostics responsive at end.
/// </summary>
[Trait("category", "soak")]
public sealed class ThirtyMinuteNoCrashSoakTests
{
    [Fact]
    public async Task ThirtyMinute_RepresentativeSession_NoCrashesNoStallsAllPopupsClosable()
    {
        var duration = SoakConfig.ThirtyMinuteDuration();
        var startUtc = DateTime.UtcNow;
        const int normalDevices = 15;
        const int subscriptionPopups = 2; // AC-6.2.4: open 2 subscription popups, leave running

        await using var harness = new SoakHarness(advertsPerSecond: 5);
        var anomalies = new List<string>();

        await harness.StartAsync(normalDevices, includeMisbehaving: true, CancellationToken.None);

        var sampleCount = SoakConfig.IsSmoke(duration) ? 4 : (int)Math.Max(4, duration.TotalMinutes / 10);
        var runner = new SoakRunner(harness, duration, sampleCount);
        await runner.RunAsync(subscriptionPopups, CancellationToken.None);

        // ── Assertions (AC-6.2.5) ──
        // Diagnostics responsive at session end: the VM still observes the live ring + the gate setter
        // still round-trips.
        var ringRef = ReferenceEquals(harness.Diagnostics.Entries, harness.RingSink.Entries);
        ringRef.Should().BeTrue("the diagnostics viewer binds the SAME live ring instance");
        harness.Diagnostics.MinSeverity = ohSpy.Core.Diagnostics.DiagSeverity.Warning;
        harness.Diagnostics.MinSeverity.Should().Be(ohSpy.Core.Diagnostics.DiagSeverity.Warning,
            "the diagnostics gate setter round-trips (responsive at session end)");
        var diagnosticsResponsive = true;

        // All opened popups dispose cleanly (closable) — no exception, idempotent.
        harness.CloseAllPopups();
        var popupsClosable = !harness.Exceptions.Any;

        // 0 UI-thread stalls > 1 s.
        var stalls = harness.Ui.StallsOverOneSecond;

        // 0 unhandled exceptions over the run.
        UnhandledExceptionCapture.FlushFinalizers();
        await harness.Ui.DrainAsync();

        // ── Write the gate-artefact report BEFORE the hard asserts so a failing run still leaves evidence ──
        await harness.FlushDiagnosticsAsync();
        var rollover = OnDiskLogInspector.Inspect(harness.DiagnosticsTempDir);
        var caps = SnapshotCaps(harness, runner);
        if (stalls.Count > 0) anomalies.Add($"{stalls.Count} UI-thread stall(s) > 1 s — UI thread blocked.");
        if (harness.Exceptions.Any) anomalies.Add($"{harness.Exceptions.Captured.Count} unhandled exception(s) captured.");

        WriteReport("30min", startUtc, duration, runner, harness, normalDevices, subscriptionPopups,
            rollover, caps, popupsClosable, diagnosticsResponsive, anomalies);

        // ── Hard gate asserts ──
        harness.Exceptions.Captured.Should().BeEmpty(
            "0 unhandled exceptions / faults may escape to the harness over the run (NFR-R1)");
        stalls.Should().BeEmpty("0 UI-thread stalls > 1 s (PumpingUiDispatcher tick-gap, AC-6.2.5)");
        popupsClosable.Should().BeTrue("every opened popup disposes cleanly (closable, AC-6.2.5)");
        diagnosticsResponsive.Should().BeTrue("DiagnosticsViewModel responsive at session end (AC-6.2.5)");
        if (!SoakConfig.IsSmoke(duration))
            rollover.FileCount.Should().BeGreaterThanOrEqualTo(2,
                "on-disk rollover MUST actually apply over a real 30-min gate run (AC-6.2.11) — a run that never crossed the 2 MB/file cap would otherwise pass silently");
    }

    internal static SoakReport.CapsSnapshot SnapshotCaps(SoakHarness harness, SoakRunner runner)
    {
        var ssdpLogCap = ShippedCaps.SsdpLogCapacity;
        var ringCap = ShippedCaps.DiagnosticRingCapacity(harness.RingSink);
        var eventListCap = runner.LivePopups.Count > 0
            ? ShippedCaps.SubscriptionEventListCapacity(runner.LivePopups[0])
            : ShippedCaps.SubscriptionEventListCapacityConst; // shipped const, not a retyped literal (⭐#3)
        var maxEvents = runner.LivePopups.Count > 0 ? runner.LivePopups.Max(p => p.Events.Count) : 0;
        var disk = OnDiskLogInspector.Inspect(harness.DiagnosticsTempDir);

        return new SoakReport.CapsSnapshot(
            SsdpLogCount: harness.SsdpLog.Entries.Count, SsdpLogCap: ssdpLogCap,
            MaxEventListCount: maxEvents, EventListCap: eventListCap,
            RingCount: harness.RingSink.Entries.Count, RingCap: ringCap,
            OnDiskBytes: disk.TotalBytes, OnDiskCapBytes: ShippedCaps.DiagnosticFileTotalCapBytes,
            OnDiskFiles: disk.FileCount, OnDiskFileCap: ShippedCaps.DiagnosticFileMaxRetained);
    }

    internal static void WriteReport(
        string title, DateTime startUtc, TimeSpan duration, SoakRunner runner, SoakHarness harness,
        int normalDevices, int subscriptionPopups, OnDiskLogInspector.Result rollover,
        SoakReport.CapsSnapshot caps, bool popupsClosable, bool diagnosticsResponsive,
        IReadOnlyList<string> anomalies)
    {
        var report = new SoakReport
        {
            Title = title,
            StartUtc = startUtc,
            ConfiguredDuration = duration,
            ActualDuration = runner.Memory.Samples.Count > 0 ? runner.Memory.Samples[^1].At : duration,
            FarmComposition = $"{normalDevices} normal + 4 misbehaving (slow/hang, byebye, partial NOTIFY, GiantScpd)",
            SubscriptionPopups = subscriptionPopups,
            MemorySamples = runner.Memory.Samples,
            AdvertsPerSecond = harness.AdvertsPerSecond,
            UnhandledExceptionCount = harness.Exceptions.Captured.Count,
            MaxDispatchGap = harness.Ui.MaxDispatchGap,
            StallCount = harness.Ui.StallsOverOneSecond.Count,
            PopupsClosable = popupsClosable,
            DiagnosticsResponsive = diagnosticsResponsive,
            Rollover = new SoakReport.RolloverResult(
                rollover.FileCount, rollover.LargestFileBytes,
                Applied: rollover.FileCount >= 2),
            Caps = caps,
            Anomalies = anomalies,
        };
        report.Write();
    }
}
