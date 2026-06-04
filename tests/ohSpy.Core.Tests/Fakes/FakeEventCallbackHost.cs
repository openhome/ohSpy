namespace ohSpy.Core.Tests.Fakes;

using System.Net;
using ohSpy.Core.Events;

/// <summary>
/// In-memory <see cref="IEventCallbackHost"/> for Story 4.2's SubscriptionClient tests. Exposes a
/// settable <see cref="CallbackBaseUrl"/> and a <see cref="RaiseNotifyAsync"/> method that drives the
/// <see cref="NotifyReceived"/> event the way the real host does — it AWAITS every subscribed handler
/// (so the non-serial / NOTIFY-before-SID race tests observe the exact host contract: the handler must
/// return promptly).
/// </summary>
internal sealed class FakeEventCallbackHost : IEventCallbackHost
{
    public Uri CallbackBaseUrl { get; set; } = new("http://127.0.0.1:54321/");

    public event Func<NotifyRequest, Task>? NotifyReceived;

    /// <summary>Raises NotifyReceived and awaits every subscribed handler (mirrors the real host's
    /// awaited dispatch). Returns once all handlers have returned.</summary>
    public async Task RaiseNotifyAsync(NotifyRequest req)
    {
        var handler = NotifyReceived;
        if (handler is null)
        {
            return;
        }

        foreach (var invocation in handler.GetInvocationList().Cast<Func<NotifyRequest, Task>>())
        {
            await invocation(req).ConfigureAwait(false);
        }
    }

    /// <summary>True once a handler has subscribed (the client subscribes in its ctor).</summary>
    public bool HasSubscriber => NotifyReceived is not null;

    public Task StartAsync(IPAddress adapterIPv4, CancellationToken ct) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
