namespace ohSpy.Core.Tests.Fakes;

using ohSpy.Core.Threading;

/// <summary>
/// <see cref="IUiDispatcher"/> fake that QUEUES posted actions instead of running them inline.
/// Where <see cref="InlineUiDispatcher"/> runs every <c>Post</c> immediately (and so masks whether
/// a VM actually marshals its UI-thread mutations), this fake defers them until <see cref="Drain"/>
/// is called — letting a test prove that observable-state mutations went THROUGH the dispatcher
/// rather than being assigned directly on the calling thread.
/// <para>Regression guard for the Story 3.2 smoke crash (2026-06-03): the invocation popup's
/// post-await continuation runs on a thread-pool thread, so a direct (un-marshalled) assignment
/// pokes <c>UIElement.Visibility</c> off-thread → <c>RPC_E_WRONGTHREAD</c> → process crash.</para>
/// <see cref="IsOnUiThread"/> returns false (the VM is being driven from a non-UI thread here).
/// </summary>
internal sealed class DeferredUiDispatcher : IUiDispatcher
{
    private readonly Queue<Action> _queue = new();

    /// <summary>Number of actions posted (never auto-run; see <see cref="Drain"/>).</summary>
    public int PostCount { get; private set; }

    public bool IsOnUiThread => false;

    public void Post(Action action)
    {
        PostCount++;
        _queue.Enqueue(action);
    }

    public Task<T> PostAsync<T>(Func<T> readback) => Task.FromResult(readback());

    public void AssertOnUiThread() { /* no-op for tests */ }

    /// <summary>Run all queued actions in FIFO order (simulates the UI thread draining its queue).</summary>
    public void Drain()
    {
        while (_queue.Count > 0)
            _queue.Dequeue()();
    }
}
