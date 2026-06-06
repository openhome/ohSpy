namespace ohSpy.Soak.Tests;

// ⚠️ SOAK FLAKE DISCIPLINE: every test in this assembly is tagged [Trait("category","soak")] and a
// soak flake is a REAL DEFECT — it is investigated and fixed, NEVER retried-until-green. NFR-R1 and
// the Scale Ceiling are not statistical claims; a single failure is a defect.

using System.Diagnostics;
using FluentAssertions;
using ohSpy.Soak.Tests.Fakes;

/// <summary>
/// Sanity tests for the <see cref="PumpingUiDispatcher"/> instrument itself (Story 6.2 Task 1).
/// These are fast structural checks (NOT the multi-hour soak) but still carry the soak trait so the
/// whole project is uniformly excluded from the default suite + chaos hook.
/// </summary>
[Trait("category", "soak")]
public sealed class PumpingUiDispatcherTests
{
    [Fact]
    public async Task BlockingAction_OverOneSecond_IsRecordedAsAStall()
    {
        using var ui = new PumpingUiDispatcher(tickInterval: TimeSpan.FromMilliseconds(50));
        ui.StartTicking();

        // Let a few clean ticks register first (no stalls yet).
        await Task.Delay(200);
        ui.StallsOverOneSecond.Should().BeEmpty("no long-running work has been queued yet");

        // Enqueue a deliberate 1.2 s blocking action ON the UI thread. While it runs, the tick
        // scheduler keeps enqueuing ticks behind it; the next tick to EXECUTE will record a gap > 1 s.
        ui.Post(() => Thread.Sleep(TimeSpan.FromMilliseconds(1200)));

        // Wait for the block to clear + the next tick to land and record.
        await Task.Delay(TimeSpan.FromMilliseconds(1600));
        await ui.DrainAsync();

        ui.StallsOverOneSecond.Should().NotBeEmpty("a 1.2 s blocking action stalls the UI thread > 1 s");
        ui.MaxDispatchGap.Should().BeGreaterThan(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task CleanRun_RecordsNoStalls()
    {
        using var ui = new PumpingUiDispatcher(tickInterval: TimeSpan.FromMilliseconds(50));
        ui.StartTicking();

        // Queue lots of tiny actions — none should stall the thread > 1 s.
        for (var i = 0; i < 200; i++)
        {
            ui.Post(() => { /* trivial */ });
            await Task.Delay(5);
        }
        await ui.DrainAsync();

        ui.StallsOverOneSecond.Should().BeEmpty("trivial queued work never stalls the UI thread > 1 s");
    }

    [Fact]
    public async Task PostAsync_RunsReadbackOnTheUiThread()
    {
        using var ui = new PumpingUiDispatcher();

        var onUiThread = await ui.PostAsync(() => ui.IsOnUiThread);

        onUiThread.Should().BeTrue("PostAsync round-trips the readback through the dedicated UI thread");
        ui.IsOnUiThread.Should().BeFalse("the test thread is NOT the UI thread");
    }

    [Fact]
    public async Task AssertOnUiThread_ThrowsOffThread_PassesOnThread()
    {
        using var ui = new PumpingUiDispatcher();

        var act = () => ui.AssertOnUiThread();
        act.Should().Throw<InvalidOperationException>("the calling thread is not the UI thread");

        // Inside a Post it must NOT throw.
        Exception? captured = null;
        await ui.PostAsync(() =>
        {
            try { ui.AssertOnUiThread(); }
            catch (Exception ex) { captured = ex; }
            return true;
        });
        captured.Should().BeNull("AssertOnUiThread passes when genuinely on the UI thread");
    }
}
