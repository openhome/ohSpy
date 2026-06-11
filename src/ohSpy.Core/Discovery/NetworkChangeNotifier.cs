namespace ohSpy.Core.Discovery;

using System;
using System.Net.NetworkInformation;

/// <summary>
/// Production <see cref="INetworkChangeNotifier"/> — a pure forwarder over the BCL static event
/// <see cref="NetworkChange.NetworkAddressChanged"/> (FR-057). Subscribes in the constructor and
/// re-raises through its own instance event; <see cref="Dispose"/> detaches the BCL handler so no
/// dangling subscriber survives app teardown (the static event roots its subscribers for process life).
/// <para>
/// <see langword="internal"/> <see langword="sealed"/> — the DI registration uses the public interface
/// (the <see cref="NetworkAdapterEnumerator"/> / <c>SsdpTransport</c> precedent). NO diagnostics / no
/// logic here: the consumer (<c>ShellViewModel</c>) owns the debounce, the decision, and the diagnostic.
/// </para>
/// </summary>
internal sealed class NetworkChangeNotifier : INetworkChangeNotifier
{
    public event EventHandler? NetworkAddressChanged;

    public NetworkChangeNotifier() =>
        NetworkChange.NetworkAddressChanged += OnBclNetworkAddressChanged;

    // The BCL event hands us (sender, EventArgs.Empty) on a non-UI thread; forward verbatim.
    private void OnBclNetworkAddressChanged(object? sender, EventArgs e) =>
        NetworkAddressChanged?.Invoke(this, EventArgs.Empty);

    public void Dispose() =>
        NetworkChange.NetworkAddressChanged -= OnBclNetworkAddressChanged;
}
