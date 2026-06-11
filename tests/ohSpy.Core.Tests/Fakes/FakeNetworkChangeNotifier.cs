namespace ohSpy.Core.Tests.Fakes;

using System;
using ohSpy.Core.Discovery;

/// <summary>
/// Test double for <see cref="INetworkChangeNotifier"/> (FR-057). The real BCL
/// <c>NetworkChange.NetworkAddressChanged</c> static event cannot be raised from a unit test — this fake
/// raises it on demand via <see cref="Raise"/>, and <see cref="RaiseOffThread"/> raises it from a
/// thread-pool thread for the mandatory Action H marshalling guard. Inert by default (never raises until
/// a test calls it), so existing <c>ShellViewModel</c> rigs that take it are unaffected.
/// <para><see cref="Dispose"/> is idempotent and merely counts calls (<see cref="DisposeCount"/>) so the
/// lifecycle test can assert the VM disposed the notifier exactly once.</para>
/// </summary>
internal sealed class FakeNetworkChangeNotifier : INetworkChangeNotifier
{
    public event EventHandler? NetworkAddressChanged;

    /// <summary>Number of times <see cref="Dispose"/> was called (lifecycle assertion, AC #11).</summary>
    public int DisposeCount { get; private set; }

    /// <summary>Synchronously raise the event (on the calling thread).</summary>
    public void Raise() => NetworkAddressChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Raise the event from a thread-pool thread (the Action H test — the production event fires
    /// off the UI thread). Returns the task so the test can await the off-thread raise completing.
    /// </summary>
    public Task RaiseOffThreadAsync() => Task.Run(Raise);

    public void Dispose() => DisposeCount++;
}
