namespace ohSpy.Core.Threading;

/// <summary>
/// Thread-marshalling contract for UI-thread access from Core. Core never touches
/// <c>Microsoft.UI.Dispatching.DispatcherQueue</c> directly; every cross-thread
/// VM mutation goes through this interface. NFR-P3 binding invariant.
/// </summary>
public interface IUiDispatcher
{
    /// <summary>Fire-and-forget marshal to UI thread.</summary>
    void Post(Action action);

    /// <summary>Round-trip: invoke <paramref name="readback"/> on the UI thread and await its result.</summary>
    Task<T> PostAsync<T>(Func<T> readback);

    /// <summary>Cheap query; safe to call from any thread.</summary>
    bool IsOnUiThread { get; }

    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> if not on the UI thread.
    /// Coding-error invariant — throws in Release as well as Debug.
    /// </summary>
    void AssertOnUiThread();
}
