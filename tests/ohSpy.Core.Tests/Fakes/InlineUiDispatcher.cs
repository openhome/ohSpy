namespace ohSpy.Core.Tests.Fakes;

using ohSpy.Core.Threading;

/// <summary>
/// Synchronous test double for <see cref="IUiDispatcher"/>. Every operation runs
/// inline on the calling thread; <see cref="IsOnUiThread"/> always returns true;
/// <see cref="AssertOnUiThread"/> is a no-op. Use in unit tests that exercise
/// dispatcher-using code without needing a real WinUI dispatcher.
/// </summary>
internal sealed class InlineUiDispatcher : IUiDispatcher
{
    public bool IsOnUiThread => true;
    public void Post(Action action) => action();
    public Task<T> PostAsync<T>(Func<T> readback) => Task.FromResult(readback());
    public void AssertOnUiThread() { /* no-op for tests */ }
}
