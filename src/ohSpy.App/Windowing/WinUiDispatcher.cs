namespace ohSpy.App.Windowing;

using Microsoft.UI.Dispatching;
using ohSpy.Core.Threading;

/// <summary>
/// WinUI 3 implementation of <see cref="IUiDispatcher"/>. Captures
/// <see cref="DispatcherQueue.GetForCurrentThread"/> at construction time —
/// MUST be constructed on the UI thread, otherwise <c>GetForCurrentThread()</c>
/// returns null and the dispatcher is unusable.
/// </summary>
internal sealed class WinUiDispatcher : IUiDispatcher
{
    private readonly DispatcherQueue _queue;

    public WinUiDispatcher()
    {
        _queue = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException(
                "WinUiDispatcher must be constructed on the UI thread. " +
                "DispatcherQueue.GetForCurrentThread() returned null.");
    }

    public bool IsOnUiThread => _queue.HasThreadAccess;

    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        bool posted = _queue.TryEnqueue(() => action());
        if (!posted)
        {
            throw new InvalidOperationException(
                "WinUiDispatcher.Post: TryEnqueue returned false. " +
                "The DispatcherQueue has been shut down.");
        }
    }

    public Task<T> PostAsync<T>(Func<T> readback)
    {
        ArgumentNullException.ThrowIfNull(readback);
        // RunContinuationsAsynchronously is REQUIRED: without it, awaiters of this Task
        // can have their continuations inlined on the UI thread inside SetResult/SetException,
        // which (a) starves the UI message pump and (b) can deadlock if the awaiter is itself
        // running on the UI thread. Do not remove this flag.
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool posted = _queue.TryEnqueue(() =>
        {
            try { tcs.SetResult(readback()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        if (!posted)
        {
            tcs.SetException(new InvalidOperationException(
                "WinUiDispatcher.PostAsync: TryEnqueue returned false. " +
                "The DispatcherQueue has been shut down."));
        }
        return tcs.Task;
    }

    public void AssertOnUiThread()
    {
        if (!IsOnUiThread)
        {
            throw new InvalidOperationException(
                "Operation must run on the UI thread. " +
                "Marshal via IUiDispatcher.Post / PostAsync.");
        }
    }
}
