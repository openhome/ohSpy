namespace ohSpy.Core.Events;

/// <summary>
/// Why a <see cref="SubscriptionHandle"/> stopped delivering events (Story 4.2, epic L1599). A lapse
/// is terminal: the renew loop has exited and a subsequent <see cref="SubscriptionHandle.CloseAsync"/>
/// sends NO UNSUBSCRIBE (the subscription is already dead on the device's side, or the device/adapter
/// is unreachable).
/// </summary>
public enum SubscriptionLapseReason
{
    /// <summary>The device refused a RENEW (HTTP 412 → <c>UpnpTransportException.StatusCode == 412</c>).</summary>
    RenewRefused,

    /// <summary>A RENEW failed transport-level (non-412 transport / timeout / protocol error).</summary>
    RenewTransportError,

    /// <summary>The adapter was switched (its token cancelled) — the device is unreachable on this adapter.</summary>
    AdapterSwitch,

    /// <summary>The device went away (byebye / prune cascaded its <c>DeviceToken</c>).</summary>
    DeviceGone,
}
