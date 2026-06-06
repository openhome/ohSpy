namespace ohSpy.Soak.Tests;

// ⚠️ STORY 6.3 — the two FARM-BACKED Performance-Budget reproducers (AC-6.3.5). These REUSE the Story 6.2
// farm primitives (FarmUpnpDevice GiantScpd 120-action; DeviceFarm advertiser/burst loop) — NOTHING new is
// built in the farm. They demonstrate, HEADLESSLY, that the two budgets that need a "busier network" than a
// dev LAN can supply are reproducible + measurable:
//   • Cold large-SCPD expand ≤ 2 s (FR-100)        — via FarmUpnpDevice GiantScpd (120-action SCPD)
//   • Sustained chatty-SSDP ≥ 20 adv/s for ≥ 30 s  — via DeviceFarm burst loop
// The MEASURED numbers are recorded in docs/verification/6.3-…md by the dev; the "no dropped frames / no UI
// freeze" eye-test stays the Project Lead's on the real ohSpy.App (a headless harness cannot judge frame
// drops — same boundary as 6.2 ⭐#1). The headless harness CAN assert 0 UI-thread stalls > 1 s (the
// PumpingUiDispatcher tick-gap instrument) + 0 unhandled exceptions, which it does below.
//
// ⚠️ [Trait("category","soak")] — excluded from the chaos hook (category=chaos) AND the quick filter
// (category!=chaos&category!=soak), NOT in ohSpy.sln, invoked BY PATH ONLY. Perf-scoped, never promoted to
// production. The sustained-burst window is time-parameterised (OHSPY_SOAK_BURST_DURATION; ~12 s smoke
// default) — set OHSPY_SOAK_BURST_DURATION=00:00:30 for the real ≥ 30 s budget assertion.

using System.Diagnostics;
using System.Globalization;
using FluentAssertions;
using ohSpy.Core.ViewModels;
using ohSpy.Soak.Tests.Harness;

[Trait("category", "soak")]
public sealed class PerformanceBudgetReproducerTests
{
    private const int GiantScpdActionCount = 120; // FarmUpnpDevice GiantScpd body
    private static readonly TimeSpan ColdLargeScpdBudget = TimeSpan.FromSeconds(2); // PRD §6 cold large-SCPD

    /// <summary>
    /// PRD §6 "Cold large-SCPD expand ≤ 2 s, no UI freeze (FR-100)". Drives the REAL ServiceNodeViewModel
    /// lazy SCPD fetch (the production path: IsExpanded=true → FetchScpdAsync → streamed action nodes)
    /// against the farm's GiantScpd (120-action) device over loopback, and times the COLD expand from the
    /// first-ever IsExpanded=true to all 120 action nodes streamed into the tree.
    /// </summary>
    [Fact]
    public async Task ColdLargeScpd_Expand_CompletesWithinBudget_ViaGiantScpdFarmDevice()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var harness = new SoakHarness(advertsPerSecond: 5);

        // includeMisbehaving:true stands up the GiantScpd device alongside the normal farm.
        await harness.StartAsync(normalDevices: 3, includeMisbehaving: true, cts.Token);

        var giant = harness.GiantScpdDevice;
        giant.Should().NotBeNull("the misbehaving farm includes the 120-action GiantScpd device");

        // Wait for the GiantScpd device's description to load + its device node to appear in the tree.
        var deviceNode = await WaitForDeviceNodeAsync(harness, giant!.Udn, TimeSpan.FromSeconds(15), cts.Token);
        deviceNode.Should().NotBeNull($"the GiantScpd device ({giant.Udn}) should populate the tree");

        // Expand the DEVICE node (lazy-builds its service children) and grab the single service node.
        await harness.Ui.PostAsync(() => { deviceNode!.IsExpanded = true; return true; });
        var serviceNode = await WaitForServiceNodeAsync(harness, deviceNode!, TimeSpan.FromSeconds(5), cts.Token);
        serviceNode.Should().NotBeNull("the GiantScpd device exposes one SoakService node to expand");

        // ── COLD expand: time from first IsExpanded=true to all 120 actions streamed in ──
        var sw = Stopwatch.StartNew();
        await harness.Ui.PostAsync(() => { serviceNode!.IsExpanded = true; return true; });
        var streamedAll = await WaitForActionCountAsync(
            harness, serviceNode!, GiantScpdActionCount, ColdLargeScpdBudget + TimeSpan.FromSeconds(3), cts.Token);
        sw.Stop();

        var actionCount = await harness.Ui.PostAsync(() => CountActionNodes(serviceNode!));

        // Settle + drain so any UI-stall tick is recorded before we assert.
        await harness.Ui.DrainAsync();
        var stalls = harness.Ui.StallsOverOneSecond;

        // ── Evidence for the report (visible in test output / -v) ──
        Console.WriteLine(
            $"[6.3 cold-large-SCPD] GiantScpd actions streamed = {actionCount.ToString(CultureInfo.InvariantCulture)}/" +
            $"{GiantScpdActionCount.ToString(CultureInfo.InvariantCulture)}; cold-expand measured = " +
            $"{sw.Elapsed.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture)} ms (budget " +
            $"{ColdLargeScpdBudget.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture)} ms); " +
            $"UI stalls > 1 s = {stalls.Count.ToString(CultureInfo.InvariantCulture)}; " +
            $"unhandled exceptions = {harness.Exceptions.Captured.Count.ToString(CultureInfo.InvariantCulture)}");

        // ── Asserts ──
        streamedAll.Should().BeTrue("all 120 GiantScpd actions stream into the tree (FR-100 incremental)");
        actionCount.Should().Be(GiantScpdActionCount, "the full 120-action SCPD renders");
        sw.Elapsed.Should().BeLessThanOrEqualTo(ColdLargeScpdBudget,
            "cold large-SCPD expand must complete within the PRD §6 ≤ 2 s budget");
        stalls.Should().BeEmpty("the cold expand must not freeze the UI thread > 1 s (FR-100 no-freeze; " +
            "the 'no dropped frames' eye-test stays the Project Lead's on the real app)");
        harness.Exceptions.Captured.Should().BeEmpty("no fault may escape the cold-expand path");
    }

    /// <summary>
    /// PRD §6 "Sustained chatty-SSDP ≥ 20 adv/s for ≥ 30 s; no dropped frames; stalls &lt; 16 ms". Runs the
    /// DeviceFarm advertiser at a ≥ 20 adv/s burst rate for the configured window and measures the ACHIEVED
    /// rate from the live SSDP-log growth, asserting the headless-observable invariants: ≥ 20 adv/s sustained,
    /// 0 UI-thread stalls > 1 s, 0 unhandled exceptions. The frame-drop eye-test is the Project Lead's.
    /// </summary>
    [Fact]
    public async Task SustainedChattySsdp_BurstLoop_SustainsAtLeast20PerSecond_NoStallsNoExceptions()
    {
        var window = BurstWindow();
        const int targetAdvertsPerSecond = 25; // ≥ 20 adv/s burst target (headroom over the 20/s floor)
        using var cts = new CancellationTokenSource(window + TimeSpan.FromMinutes(1));

        await using var harness = new SoakHarness(advertsPerSecond: targetAdvertsPerSecond);
        // A modest farm: the round-robin advertiser saturates the channel regardless of device count.
        await harness.StartAsync(normalDevices: 10, includeMisbehaving: false, cts.Token);
        await harness.WaitForDevicesAsync(1, TimeSpan.FromSeconds(8), cts.Token);

        // ── Measure achieved adv/s over the sustained window from SSDP-log growth ──
        var startCount = await harness.Ui.PostAsync(() => harness.SsdpLog.Entries.Count);
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < window && !cts.IsCancellationRequested)
        {
            await Task.Delay(100, cts.Token);
        }
        sw.Stop();
        await harness.Ui.DrainAsync();
        var endCount = await harness.Ui.PostAsync(() => harness.SsdpLog.Entries.Count);

        var observed = Math.Max(0, endCount - startCount);
        var achievedPerSecond = observed / sw.Elapsed.TotalSeconds;
        var stalls = harness.Ui.StallsOverOneSecond;

        Console.WriteLine(
            $"[6.3 chatty-SSDP] window = {sw.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)} s; " +
            $"adverts observed = {observed.ToString(CultureInfo.InvariantCulture)}; achieved = " +
            $"{achievedPerSecond.ToString("F1", CultureInfo.InvariantCulture)} adv/s (target ≥ 20); " +
            $"UI stalls > 1 s = {stalls.Count.ToString(CultureInfo.InvariantCulture)}; " +
            $"unhandled exceptions = {harness.Exceptions.Captured.Count.ToString(CultureInfo.InvariantCulture)}");

        achievedPerSecond.Should().BeGreaterThanOrEqualTo(20.0,
            "the farm burst loop must sustain ≥ 20 adv/s (PRD §6 chatty-SSDP; complement to 6.1.14)");
        stalls.Should().BeEmpty("sustained chatty SSDP must not stall the UI thread > 1 s (the 'no dropped " +
            "frames' / stalls < 16 ms eye-test stays the Project Lead's on the real app)");
        harness.Exceptions.Captured.Should().BeEmpty("no fault may escape under sustained SSDP load");
    }

    // ── Helpers (all tree reads marshalled onto the soak UI thread) ──

    private static async Task<DeviceNodeViewModel?> WaitForDeviceNodeAsync(
        SoakHarness harness, string udn, TimeSpan budget, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + budget;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            var node = await harness.Ui.PostAsync(() =>
                harness.Shell.DeviceTree.Devices.TryGetItem(udn, out var n) ? n : null);
            if (node is not null)
            {
                return node;
            }
            await Task.Delay(50, ct).ConfigureAwait(false);
        }
        return null;
    }

    private static async Task<ServiceNodeViewModel?> WaitForServiceNodeAsync(
        SoakHarness harness, DeviceNodeViewModel deviceNode, TimeSpan budget, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + budget;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            var svc = await harness.Ui.PostAsync(() =>
                deviceNode.Children.OfType<ServiceNodeViewModel>().FirstOrDefault());
            if (svc is not null)
            {
                return svc;
            }
            await Task.Delay(50, ct).ConfigureAwait(false);
        }
        return null;
    }

    private static async Task<bool> WaitForActionCountAsync(
        SoakHarness harness, ServiceNodeViewModel serviceNode, int target, TimeSpan budget, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + budget;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            var count = await harness.Ui.PostAsync(() => CountActionNodes(serviceNode));
            if (count >= target)
            {
                return true;
            }
            await Task.Delay(15, ct).ConfigureAwait(false);
        }
        return false;
    }

    // Runs ON the UI thread. Counts streamed action nodes (excludes the loading placeholder + inline errors).
    private static int CountActionNodes(ServiceNodeViewModel serviceNode) =>
        serviceNode.Children.OfType<ActionNodeViewModel>().Count();

    /// <summary>The sustained-burst window: OHSPY_SOAK_BURST_DURATION (e.g. 00:00:30 for the real ≥ 30 s
    /// budget) or a ~12 s structural-smoke default (long enough to measure a stable rate quickly).</summary>
    private static TimeSpan BurstWindow()
    {
        var raw = Environment.GetEnvironmentVariable("OHSPY_SOAK_BURST_DURATION");
        if (!string.IsNullOrWhiteSpace(raw))
        {
            if (TimeSpan.TryParse(raw, CultureInfo.InvariantCulture, out var ts) && ts > TimeSpan.Zero)
            {
                return ts;
            }
            throw new FormatException(
                $"OHSPY_SOAK_BURST_DURATION='{raw}' is not a valid positive TimeSpan (e.g. 00:00:30).");
        }
        return TimeSpan.FromSeconds(12);
    }
}
