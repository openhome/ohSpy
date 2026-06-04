namespace ohSpy.Core.Tests.Events;

using System.Text;
using FluentAssertions;
using ohSpy.Core.Events;
using ohSpy.Core.Tests.Fakes;

/// <summary>
/// Story 4.1 — <see cref="TimeoutStream"/> idle-read budget enforcement (AC-4.1.9). Unit-tested
/// directly against a <see cref="HangingStream"/> (never-completing read) and a
/// <see cref="MemoryStream"/> (prompt read). Carries <c>[Trait("ac", "AC-4.1.9")]</c>.
/// </summary>
public sealed class TimeoutStreamTests
{
    [Fact]
    [Trait("ac", "AC-4.1.9")]
    public async Task Read_IdleBeyondBudget_ThrowsCallbackTimeoutException()
    {
        await using var inner = new HangingStream();
        await using var stream = new TimeoutStream(inner, TimeSpan.FromMilliseconds(50));

        var buf = new byte[16];
        var act = async () => await stream.ReadAsync(buf);

        await act.Should().ThrowAsync<CallbackTimeoutException>();
    }

    [Fact]
    [Trait("ac", "AC-4.1.9")]
    public async Task Read_PromptRead_ReturnsBytesWithinBudget()
    {
        var payload = Encoding.ASCII.GetBytes("hello");
        await using var inner = new MemoryStream(payload);
        await using var stream = new TimeoutStream(inner, TimeSpan.FromSeconds(5));

        var buf = new byte[16];
        var read = await stream.ReadAsync(buf);

        read.Should().Be(5);
        Encoding.ASCII.GetString(buf, 0, read).Should().Be("hello");
    }

    [Fact]
    [Trait("ac", "AC-4.1.9")]
    public async Task Read_CallerTokenCancelled_PropagatesOperationCanceled_NotTimeout()
    {
        // Caller cancellation (adapter/app shutdown) must surface as OperationCanceledException —
        // the normal teardown path — NOT the timeout sentinel (D4↔D7 composition).
        await using var inner = new HangingStream();
        await using var stream = new TimeoutStream(inner, TimeSpan.FromSeconds(30));
        using var cts = new CancellationTokenSource();

        var buf = new byte[16];
        var readTask = stream.ReadAsync(buf, cts.Token).AsTask();
        await cts.CancelAsync();

        var act = async () => await readTask;
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    [Trait("ac", "AC-4.1.9")]
    public async Task ActiveBudget_SwitchesBetweenReads()
    {
        // The parser flips ActiveBudget from headers → body; confirm a later read honours the new value.
        await using var inner = new HangingStream();
        await using var stream = new TimeoutStream(inner, TimeSpan.FromSeconds(30)) { ActiveBudget = TimeSpan.FromMilliseconds(40) };

        var buf = new byte[16];
        var act = async () => await stream.ReadAsync(buf);
        await act.Should().ThrowAsync<CallbackTimeoutException>();
    }
}
