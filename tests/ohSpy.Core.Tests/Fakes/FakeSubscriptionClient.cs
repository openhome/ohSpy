namespace ohSpy.Core.Tests.Fakes;

using ohSpy.Core.Devices;
using ohSpy.Core.Events;
using ohSpy.Core.Http;
using ohSpy.Core.Models;

/// <summary>
/// Controllable <see cref="ISubscriptionClient"/> for <c>SubscriptionPopupViewModel</c> tests. By
/// default <see cref="SubscribeAsync"/> returns a fresh <see cref="SubscriptionHandle"/> (newed via
/// the <c>internal</c> ctor — <c>ohSpy.Core.Tests</c> has <c>InternalsVisibleTo</c>) whose
/// <c>CloseAsync</c> increments <see cref="CloseCount"/>; the test drives the handle's <c>internal</c>
/// <c>RaiseNotification</c>/<c>RaiseLapsed</c> directly (the 4.2 SubscriptionClientTests precedent).
/// <para>
/// Set <see cref="ThrowOnSubscribe"/> to make <see cref="SubscribeAsync"/> throw (AC-4.3.5
/// failed-subscribe). Set <see cref="SubscribeGate"/> to a non-completed task to hold the subscribe
/// flow open (e.g. to exercise the off-thread continuation / cancellation). Each call captures the
/// (service, entry, token) it was handed.
/// </para>
/// </summary>
internal sealed class FakeSubscriptionClient : ISubscriptionClient
{
    private int _closeCount;
    private int _sidCounter;

    /// <summary>If non-null, <see cref="SubscribeAsync"/> throws this instead of returning a handle.</summary>
    public Exception? ThrowOnSubscribe { get; set; }

    /// <summary>If set, the subscribe flow awaits this before returning the handle (off-thread drill).</summary>
    public Task? SubscribeGate { get; set; }

    /// <summary>Total <c>CloseAsync</c> calls across all handles this client handed out (CloseAsync is idempotent).</summary>
    public int CloseCount => Volatile.Read(ref _closeCount);

    /// <summary>The handle returned by the most recent successful <see cref="SubscribeAsync"/>.</summary>
    public SubscriptionHandle? LastHandle { get; private set; }

    public List<(ServiceDescription Service, RegistryEntry Entry, CancellationToken Token)> Calls { get; } = new();

    public CancellationToken AdapterContext { get; private set; }

    public void SetAdapterContext(CancellationToken adapterToken) => AdapterContext = adapterToken;

    public async Task<SubscriptionHandle> SubscribeAsync(
        ServiceDescription service, RegistryEntry parentEntry, CancellationToken popupToken)
    {
        Calls.Add((service, parentEntry, popupToken));

        if (SubscribeGate is { } gate)
            await gate.ConfigureAwait(false);

        popupToken.ThrowIfCancellationRequested();

        if (ThrowOnSubscribe is { } ex)
            throw ex;

        var sid = $"uuid:fake-sid-{Interlocked.Increment(ref _sidCounter)}";
        var handle = new SubscriptionHandle(sid, () =>
        {
            Interlocked.Increment(ref _closeCount);
            return Task.CompletedTask;
        });
        LastHandle = handle;
        return handle;
    }

    /// <summary>Convenience: build (but do NOT register) a free-standing handle for direct-drive tests.</summary>
    public static SubscriptionHandle NewHandle(string sid = "uuid:fake-sid", Action? onClose = null) =>
        new(sid, () => { onClose?.Invoke(); return Task.CompletedTask; });
}
