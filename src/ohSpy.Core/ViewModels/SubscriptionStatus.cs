namespace ohSpy.Core.ViewModels;

/// <summary>
/// Lifecycle state of a <see cref="SubscriptionPopupViewModel"/> (Story 4.3, AC-4.3.1). The App
/// window code-behind projects this enum to banner / status-indicator visibility (Pattern 2 keeps
/// <c>Visibility</c>/<c>Brush</c> out of Core; <c>CoreAppBoundaryTests</c> enforces).
/// </summary>
public enum SubscriptionStatus
{
    /// <summary>SUBSCRIBE is in flight (set synchronously in the ctor, on the UI thread).</summary>
    Subscribing,

    /// <summary>SUBSCRIBE succeeded; events are streaming (the granted-timeout detail is on StatusMessage).</summary>
    Subscribed,

    /// <summary>The subscription lapsed (renew refused/failed, or adapter switch). The popup stays open.</summary>
    Lapsed,

    /// <summary>The device went away (byebye / prune, or <c>Lapsed(DeviceGone)</c>). Already-shown data stays.</summary>
    DeviceGone,

    /// <summary>SUBSCRIBE failed (transport / timeout / protocol). No handle, no UNSUBSCRIBE on close.</summary>
    FailedToSubscribe,
}
