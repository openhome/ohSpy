namespace ohSpy.Core.Events;

/// <summary>
/// Distinguishable sentinel thrown by <see cref="TimeoutStream"/> when a read's idle time
/// exceeds the <em>active</em> budget (headers or body), as opposed to a genuine
/// adapter/app cancellation (which surfaces as <see cref="OperationCanceledException"/> and
/// is the normal shutdown path). The host maps this to either
/// <see cref="ohSpy.Core.Diagnostics.DiagCategories.GenaCallbackHeadersTo"/> or
/// <see cref="ohSpy.Core.Diagnostics.DiagCategories.GenaCallbackBodyTo"/> depending on the
/// phase the stream was in when it fired (Decision 4, AC-4.1.7/AC-4.1.8/AC-4.1.9).
/// </summary>
internal sealed class CallbackTimeoutException : Exception
{
    public CallbackTimeoutException(string message) : base(message) { }

    public CallbackTimeoutException() { }

    public CallbackTimeoutException(string message, Exception innerException)
        : base(message, innerException) { }
}
