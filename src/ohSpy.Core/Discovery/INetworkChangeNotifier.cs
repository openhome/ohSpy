namespace ohSpy.Core.Discovery;

using System;

/// <summary>
/// Test-fakeable abstraction over <c>System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged</c>
/// (FR-057). The BCL static event cannot be raised by a unit test and roots its subscribers for process
/// life, so Core consumes this seam instead (the <see cref="INetworkAdapterEnumerator"/> / IClock
/// testability pattern). The event is raised on a NON-UI thread — consumers MUST marshal any
/// observable-state mutation via <c>IUiDispatcher.Post</c> (Action H, memory winui-no-synccontext-marshal-vm).
/// <para>
/// <see cref="IDisposable"/> so the BCL static-event subscription is detached on app teardown — a leaked
/// handler on the process-global <c>NetworkChange</c> event is a classic memory leak (it roots its
/// subscribers for the life of the process).
/// </para>
/// </summary>
public interface INetworkChangeNotifier : IDisposable
{
    /// <summary>
    /// Raised when the host's network address configuration changes (an adapter's IPv4 changes, or an
    /// adapter is added/removed/enabled/disabled). Fires on a non-UI thread; a transition produces a
    /// BURST of these, so consumers debounce. Pure forwarder — carries no payload (the consumer
    /// re-enumerates eligible adapters to decide what actually changed).
    /// </summary>
    event EventHandler NetworkAddressChanged;
}
