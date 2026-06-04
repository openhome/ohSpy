namespace ohSpy.Core.Tests.Fakes;

using ohSpy.Core.Threading;

/// <summary>
/// <see cref="IUiDispatcher"/> fake whose <see cref="PostAsync{T}"/> PARKS (awaits a release gate)
/// before running the readback — so a Story 5.2 switch test can hold a switch open at the
/// registry/log-clear <c>PostAsync</c> and prove the re-entrancy guard rejects a concurrent switch.
/// <see cref="Post"/> runs inline (the transient flips are not the subject of these tests).
/// <para><see cref="WaitForGateAsync"/> completes once the parked call has been reached;
/// <see cref="OpenGate"/> releases it so the switch completes.</para>
/// </summary>
internal sealed class GatedUiDispatcher : IUiDispatcher
{
    private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsOnUiThread => true;

    public void Post(Action action) => action();

    public async Task<T> PostAsync<T>(Func<T> readback)
    {
        _reached.TrySetResult();      // signal the test that the switch reached the clear
        await _release.Task.ConfigureAwait(false); // park until the test opens the gate
        return readback();
    }

    public void AssertOnUiThread() { /* no-op for tests */ }

    /// <summary>Completes once the parked <see cref="PostAsync{T}"/> has been entered.</summary>
    public Task WaitForGateAsync() => _reached.Task;

    /// <summary>Releases the parked <see cref="PostAsync{T}"/> so the switch finishes.</summary>
    public void OpenGate() => _release.TrySetResult();
}
