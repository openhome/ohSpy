namespace ohSpy.Soak.Tests.Harness;

using System.Diagnostics;
using ohSpy.Core.Devices;
using ohSpy.Core.ViewModels;

/// <summary>
/// Story 6.2 Task 3 — the representative-session-script driver (AC-6.2.4). Drives the REAL Core stack
/// through the ⭐#1 script CONTINUOUSLY for the configured duration: startup/bind, tree populate, expand
/// services, invoke actions (succeed / SOAP-fault / timeout), open subscription popups and leave them
/// running, open+close diagnostics, switch adapter once, rescan twice — then per-iteration churn
/// (rescan, NOTIFY emission, byebye/partial-NOTIFY misbehaving cases) so the bounded collections
/// saturate and the on-disk log rolls.
/// <para>The same script runs for the ~10 s structural smoke and the gate run — only the duration +
/// memory-sample cadence scale.</para>
/// </summary>
internal sealed class SoakRunner
{
    private readonly SoakHarness _harness;
    private readonly TimeSpan _duration;
    private readonly int _memorySampleCount;
    private readonly List<SubscriptionPopupViewModel> _livePopups = new();

    public MemorySampler Memory { get; } = new();

    public SoakRunner(SoakHarness harness, TimeSpan duration, int memorySampleCount)
    {
        _harness = harness;
        _duration = duration;
        _memorySampleCount = Math.Max(2, memorySampleCount);
    }

    /// <summary>Run the full representative session for the configured duration.</summary>
    public async Task RunAsync(int subscriptionPopupCount, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        Memory.Take(); // t0 warm-up sample

        // ── One-time script prologue: populate tree, expand, invoke, open popups, diagnostics, switch ──
        await PopulateAndExpandAsync(ct).ConfigureAwait(false);
        await InvokeActionsAsync(ct).ConfigureAwait(false);
        await OpenSubscriptionPopupsAsync(subscriptionPopupCount, ct).ConfigureAwait(false);
        ExerciseDiagnostics();
        await _harness.SwitchAdapterAsync().ConfigureAwait(false);

        // After the switch the registry/log cleared; re-burst + re-open popups so the steady-state load
        // (devices + live popups) holds for the rest of the run.
        await _harness.Farm.BurstAliveAsync(ct).ConfigureAwait(false);
        await _harness.WaitForDevicesAsync(1, TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        _livePopups.Clear();
        await OpenSubscriptionPopupsAsync(subscriptionPopupCount, ct).ConfigureAwait(false);

        // Two rescans (AC-6.2.4).
        await _harness.RescanAsync().ConfigureAwait(false);
        await Task.Delay(150, ct).ConfigureAwait(false);
        await _harness.RescanAsync().ConfigureAwait(false);

        // ── Steady-state churn loop until the duration elapses ──
        var nextSampleAt = TimeSpan.FromTicks(_duration.Ticks / _memorySampleCount);
        var sampleInterval = nextSampleAt;
        var iteration = 0;
        var byebyeSent = false;

        while (sw.Elapsed < _duration && !ct.IsCancellationRequested)
        {
            iteration++;

            // Emit NOTIFY to the subscription popups so their event lists fill toward the 5,000 cap.
            await EmitNotifyBurstAsync(ct).ConfigureAwait(false);

            // Partial NOTIFY (misbehaving device) — the callback host / parser must tolerate it.
            await EmitPartialNotifyAsync(ct).ConfigureAwait(false);

            // Mid-interaction byebye exactly once (FR-037 cascade to any open popup on that device).
            if (!byebyeSent && _harness.Farm.ByebyeDevice is { } byebye)
            {
                await _harness.Farm.SendByebyeAsync(byebye, ct).ConfigureAwait(false);
                byebyeSent = true;
            }

            // Periodic rescan keeps the registry churning + the SSDP log saturating.
            if (iteration % 5 == 0)
            {
                await _harness.RescanAsync().ConfigureAwait(false);
            }

            // Memory sampling on the run-relative cadence.
            if (sw.Elapsed >= nextSampleAt)
            {
                Memory.Take();
                nextSampleAt += sampleInterval;
            }

            await Task.Delay(20, ct).ConfigureAwait(false);
        }

        // Final settle + final memory sample.
        await _harness.Ui.DrainAsync().ConfigureAwait(false);
        Memory.Take();
    }

    public IReadOnlyList<SubscriptionPopupViewModel> LivePopups => _livePopups;

    private async Task PopulateAndExpandAsync(CancellationToken ct)
    {
        await _harness.WaitForDevicesAsync(1, TimeSpan.FromSeconds(8), ct).ConfigureAwait(false);

        // Expand every loaded device node (lazy-build its services) + every service node.
        var nodes = _harness.Shell.DeviceTree.Devices.ToArray();
        foreach (var node in nodes)
        {
            if (node is DeviceNodeViewModel deviceNode)
            {
                await _harness.Ui.PostAsync(() => { deviceNode.IsExpanded = true; return true; }).ConfigureAwait(false);
                foreach (var child in deviceNode.Children.ToArray())
                {
                    if (child is ServiceNodeViewModel serviceNode)
                    {
                        await _harness.Ui.PostAsync(() => { serviceNode.IsExpanded = true; return true; }).ConfigureAwait(false);
                    }
                }
            }
        }
        // Give the GiantScpd cold-expand + any slow-responder fetch a chance to run (then move on).
        await Task.Delay(200, ct).ConfigureAwait(false);
    }

    private async Task InvokeActionsAsync(CancellationToken ct)
    {
        // Invoke a Ping against the first loaded device's first service: succeed path (normal device)
        // and timeout path (slow responder) — both exercise the real SOAP over loopback.
        foreach (var entry in _harness.Registry.Loaded.Take(2))
        {
            var services = entry.Description?.Services;
            if (services is null || services.Count == 0)
            {
                continue;
            }
            var popup = _harness.BuildInvocationPopup(services[0], entry);
            try
            {
                await popup.InitializeAsync().ConfigureAwait(false);
                // Fire the invoke command (real SOAP POST). Fire-and-forget — the slow device times out.
                if (popup.InvokeCommand.CanExecute(null))
                {
                    popup.InvokeCommand.Execute(null);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // An invoke failure is an expected operational outcome (fault/timeout), not a soak crash;
                // the popup surfaces it internally. Record nothing here.
            }
            finally
            {
                await Task.Delay(20, ct).ConfigureAwait(false);
                popup.Dispose(); // closable
            }
        }
    }

    private async Task OpenSubscriptionPopupsAsync(int count, CancellationToken ct)
    {
        var loaded = _harness.Registry.Loaded.Take(count).ToArray();
        foreach (var entry in loaded)
        {
            var popup = await _harness.OpenSubscriptionPopupAsync(entry).ConfigureAwait(false);
            if (popup is not null)
            {
                _livePopups.Add(popup);
            }
            await Task.Delay(10, ct).ConfigureAwait(false);
        }
    }

    private void ExerciseDiagnostics()
    {
        // "open + close diagnostic viewer" + "responsive at session end": the VM observes the live ring
        // and the gate setter round-trips. Toggle the gate to prove the round-trip works.
        var original = _harness.Diagnostics.MinSeverity;
        _harness.Diagnostics.MinSeverity = ohSpy.Core.Diagnostics.DiagSeverity.Verbose;
        _harness.Diagnostics.MinSeverity = original;
    }

    private async Task EmitNotifyBurstAsync(CancellationToken ct)
    {
        var callback = _harness.CurrentCallbackHost;
        if (callback is null)
        {
            return;
        }
        Uri baseUrl;
        try { baseUrl = callback.CallbackBaseUrl; }
        catch (InvalidOperationException) { return; } // host not started yet

        // Emit a few NOTIFYs per loaded subscribed device so the event lists fill.
        foreach (var device in _harness.Farm.Devices.Take(8))
        {
            await device.EmitNotifyAsync(baseUrl, partial: false, ct).ConfigureAwait(false);
        }
    }

    private async Task EmitPartialNotifyAsync(CancellationToken ct)
    {
        var callback = _harness.CurrentCallbackHost;
        if (callback is null || _harness.Farm.PartialNotifyDevice is not { } device)
        {
            return;
        }
        Uri baseUrl;
        try { baseUrl = callback.CallbackBaseUrl; }
        catch (InvalidOperationException) { return; }
        await device.EmitNotifyAsync(baseUrl, partial: true, ct).ConfigureAwait(false);
    }
}
