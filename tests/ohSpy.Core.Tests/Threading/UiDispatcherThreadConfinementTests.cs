namespace ohSpy.Core.Tests.Threading;

using FluentAssertions;
using ohSpy.Core.Tests.Fakes;
using ohSpy.Core.Threading;

public class UiDispatcherThreadConfinementTests
{
    [Fact]
    [Trait("ac", "AC-3")]
    public void InlineUiDispatcher_AlwaysReportsOnUiThread()
    {
        var d = new InlineUiDispatcher();
        d.IsOnUiThread.Should().BeTrue();
        Action assert = () => d.AssertOnUiThread();
        assert.Should().NotThrow();
    }

    [Fact]
    [Trait("ac", "AC-3")]
    public void InlineUiDispatcher_PostExecutesSynchronously()
    {
        var d = new InlineUiDispatcher();
        bool ran = false;
        d.Post(() => ran = true);
        ran.Should().BeTrue();
    }

    [Fact]
    [Trait("ac", "AC-3")]
    public async Task InlineUiDispatcher_PostAsyncReturnsCompletedTaskWithReadbackResult()
    {
        var d = new InlineUiDispatcher();
        var t = d.PostAsync(() => 42);
        t.IsCompletedSuccessfully.Should().BeTrue();
        int value = await t;
        value.Should().Be(42);
    }

    [Fact]
    [Trait("ac", "AC-6")]
    public void OffThreadDispatcher_AssertOnUiThread_Throws()
    {
        var d = new OffThreadDispatcher();
        Action act = () => d.AssertOnUiThread();
        act.Should().Throw<InvalidOperationException>();
    }

    /// <summary>
    /// Documented test-double demonstrating the dispatcher-violation contract: a dispatcher
    /// that knows it is off-thread MUST throw from <see cref="IUiDispatcher.AssertOnUiThread"/>.
    /// The collections themselves do not enforce; callers invoke <c>AssertOnUiThread()</c> at
    /// their mutation site and rely on the dispatcher's truthful answer.
    /// </summary>
    private sealed class OffThreadDispatcher : IUiDispatcher
    {
        public bool IsOnUiThread => false;
        public void Post(Action action) => throw new NotImplementedException();
        public Task<T> PostAsync<T>(Func<T> readback) => throw new NotImplementedException();
        public void AssertOnUiThread()
        {
            if (!IsOnUiThread)
            {
                throw new InvalidOperationException(
                    "Operation must run on the UI thread. Marshal via IUiDispatcher.Post / PostAsync.");
            }
        }
    }
}
