namespace ohSpy.Soak.Tests.Harness;

using System.Collections.Concurrent;

/// <summary>
/// Story 6.2 (AC-6.2.5 / AC-6.2.9) — captures any exception that escapes to the process: an
/// <see cref="AppDomain.UnhandledException"/>, a <see cref="TaskScheduler.UnobservedTaskException"/>,
/// or a fault on the soak UI thread. ANY captured exception FAILS the soak (0 unhandled exceptions
/// is the assertion). A soak flake is a real defect — these are investigated, never retried.
/// <para>
/// The AppDomain / TaskScheduler handlers are process-global; this type installs them on construction
/// and removes them on <see cref="Dispose"/> so a soak test never leaks its capture into another test.
/// </para>
/// </summary>
internal sealed class UnhandledExceptionCapture : IDisposable
{
    private readonly ConcurrentQueue<Exception> _captured = new();

    public UnhandledExceptionCapture()
    {
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandled;
        TaskScheduler.UnobservedTaskException += OnUnobservedTask;
    }

    public IReadOnlyList<Exception> Captured => _captured.ToArray();

    public bool Any => !_captured.IsEmpty;

    /// <summary>Hook a source of exceptions (e.g. the PumpingUiDispatcher's UI-thread fault event).</summary>
    public void Record(Exception ex) => _captured.Enqueue(ex);

    private void OnAppDomainUnhandled(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            _captured.Enqueue(ex);
        }
    }

    private void OnUnobservedTask(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _captured.Enqueue(e.Exception);
        // Mark observed so it doesn't escalate the process — we've recorded it as a soak failure.
        e.SetObserved();
    }

    /// <summary>Force a GC + finalization sweep so any abandoned faulted Task surfaces its
    /// UnobservedTaskException BEFORE the assertion reads <see cref="Captured"/>.</summary>
    public static void FlushFinalizers()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    public void Dispose()
    {
        AppDomain.CurrentDomain.UnhandledException -= OnAppDomainUnhandled;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTask;
    }
}
