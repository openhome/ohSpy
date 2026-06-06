namespace ohSpy.Soak.Tests.Fakes;

using System.Collections.Concurrent;
using System.Diagnostics;
using ohSpy.Core.Threading;

/// <summary>
/// Story 6.2 — the UI-STALL INSTRUMENT. A real dedicated "UI thread" running a serial work queue,
/// implementing <see cref="IUiDispatcher"/>. This is the only fake that can measure "0 UI-thread
/// stalls &gt; 1 s" headlessly:
/// <list type="bullet">
///   <item>
///     <b>Marshalling is genuinely exercised.</b> Unlike the shipped <c>InlineUiDispatcher</c> (which
///     runs <c>Post</c> inline on the calling thread and so MASKS marshalling — see MEMORY
///     <c>winui-no-synccontext-marshal-vm</c>), every off-thread <c>await</c> continuation that calls
///     <see cref="Post"/> here is genuinely re-queued onto this one dedicated thread, exactly as the
///     real WinUI <c>DispatcherQueue</c> does. An un-marshalled VM mutation would either trip
///     <see cref="AssertOnUiThread"/> or race a collection — either way the soak surfaces it.
///   </item>
///   <item>
///     <b>UI-stall is observable.</b> A periodic "tick" action is enqueued on a wall-clock cadence
///     (default 100 ms); the gap between successive tick EXECUTIONS is measured. A gap &gt; 1 s means
///     the UI thread was blocked &gt; 1 s by some other queued work — exactly the "dispatcher-tick
///     timing" the epic AC names. Read <see cref="StallsOverOneSecond"/> / <see cref="MaxDispatchGap"/>.
///   </item>
/// </list>
/// <para>
/// This is a TEST fake, not a production seam — it lives only in the soak assembly. The shipped
/// <c>DeferredUiDispatcher</c> only manual-drains and <c>InlineUiDispatcher</c> runs inline; neither
/// can time a stall, which is why this primitive exists.
/// </para>
/// </summary>
internal sealed class PumpingUiDispatcher : IUiDispatcher, IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _uiThread;
    private readonly CancellationTokenSource _shutdown = new();

    // Tick instrumentation. The tick scheduler runs on a side thread and enqueues a tick action onto
    // the UI queue every _tickInterval; the tick action (executed ON the UI thread) records the gap
    // since the previous tick execution. A long-running queued action delays the next tick's execution
    // → a measurable gap > 1 s.
    private readonly TimeSpan _tickInterval;
    private readonly Thread _tickScheduler;
    private long _lastTickTicks;          // Stopwatch ticks of the previous tick EXECUTION
    private long _maxGapTicks;            // largest observed inter-tick gap
    private readonly ConcurrentQueue<TimeSpan> _stalls = new(); // gaps that exceeded 1 s
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private int _started;

    private static readonly TimeSpan StallThreshold = TimeSpan.FromSeconds(1);

    public PumpingUiDispatcher(TimeSpan? tickInterval = null)
    {
        _tickInterval = tickInterval ?? TimeSpan.FromMilliseconds(100);

        _uiThread = new Thread(UiThreadLoop)
        {
            IsBackground = true,
            Name = "soak-ui-thread",
        };
        _uiThread.Start();

        _tickScheduler = new Thread(TickSchedulerLoop)
        {
            IsBackground = true,
            Name = "soak-ui-tick-scheduler",
        };
    }

    /// <summary>Begin the periodic tick instrumentation. Call once the harness is wired and the run
    /// window starts (so warm-up queue churn before the run does not count as a stall).</summary>
    public void StartTicking()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return;
        }
        Volatile.Write(ref _lastTickTicks, _clock.Elapsed.Ticks);
        _tickScheduler.Start();
    }

    public bool IsOnUiThread => Thread.CurrentThread == _uiThread;

    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_queue.IsAddingCompleted)
        {
            return; // shutting down — drop (mirrors a torn-down DispatcherQueue)
        }
        try
        {
            _queue.Add(action);
        }
        catch (InvalidOperationException)
        {
            // CompleteAdding raced us during teardown — tolerated.
        }
    }

    public Task<T> PostAsync<T>(Func<T> readback)
    {
        ArgumentNullException.ThrowIfNull(readback);
        // Round-trip through the queue so the readback genuinely runs ON the UI thread (a real
        // DispatcherQueue.TryEnqueue round-trip), and the caller awaits its result.
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            try { tcs.SetResult(readback()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }

    public void AssertOnUiThread()
    {
        if (!IsOnUiThread)
        {
            throw new InvalidOperationException(
                "Not on the soak UI thread — a Core VM mutated observable state off-thread without marshalling " +
                "(winui-no-synccontext-marshal-vm). This is the production RPC_E_WRONGTHREAD crash class.");
        }
    }

    /// <summary>The largest inter-tick gap observed (proxy for the worst UI-thread stall).</summary>
    public TimeSpan MaxDispatchGap => TimeSpan.FromTicks(Volatile.Read(ref _maxGapTicks));

    /// <summary>Every inter-tick gap that exceeded 1 s — AC-6.2.5 asserts this is empty.</summary>
    public IReadOnlyList<TimeSpan> StallsOverOneSecond => _stalls.ToArray();

    /// <summary>Round-trip a no-op through the queue so the caller can await the UI thread draining
    /// everything enqueued so far (deterministic settle point for assertions).</summary>
    public Task DrainAsync() => PostAsync(() => true);

    private void UiThreadLoop()
    {
        try
        {
            foreach (var action in _queue.GetConsumingEnumerable(_shutdown.Token))
            {
                try
                {
                    action();
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // A faulting queued action MUST NOT kill the UI pump (the real DispatcherQueue keeps
                    // running). Re-surface it through the unhandled-exception channel the harness watches
                    // so it fails the soak as a real defect — never silently swallowed.
                    UiThreadException?.Invoke(ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    /// <summary>Raised when a queued action faults on the UI thread. The harness wires this to its
    /// unhandled-exception capture so an off-thread crash class fails the soak.</summary>
    public event Action<Exception>? UiThreadException;

    private void TickSchedulerLoop()
    {
        var token = _shutdown.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                Thread.Sleep(_tickInterval);
                if (token.IsCancellationRequested)
                {
                    return;
                }
                Post(RecordTick);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Tick scheduler is best-effort instrumentation; never let it crash the run.
        }
    }

    // Runs ON the UI thread (enqueued by the scheduler). Measures the gap since the previous tick
    // EXECUTION — a long queued action ahead of this tick inflates the gap.
    private void RecordTick()
    {
        var now = _clock.Elapsed.Ticks;
        var prev = Volatile.Read(ref _lastTickTicks);
        Volatile.Write(ref _lastTickTicks, now);

        var gap = TimeSpan.FromTicks(now - prev);
        if (gap.Ticks > Volatile.Read(ref _maxGapTicks))
        {
            Volatile.Write(ref _maxGapTicks, gap.Ticks);
        }
        if (gap > StallThreshold)
        {
            _stalls.Enqueue(gap);
        }
    }

    public void Dispose()
    {
        try { _shutdown.Cancel(); } catch (ObjectDisposedException) { }
        _queue.CompleteAdding();
        // Best-effort join so background threads exit before the process moves on.
        if (_uiThread.IsAlive)
        {
            _uiThread.Join(TimeSpan.FromSeconds(2));
        }
        if (_tickScheduler.IsAlive)
        {
            _tickScheduler.Join(TimeSpan.FromSeconds(2));
        }
        _queue.Dispose();
        _shutdown.Dispose();
    }
}
