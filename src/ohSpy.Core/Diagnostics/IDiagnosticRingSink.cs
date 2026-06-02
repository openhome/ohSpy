namespace ohSpy.Core.Diagnostics;

using ohSpy.Core.Collections;

/// <summary>
/// In-memory bounded sink for diagnostic entries — drives the FR-041 live viewer. Holds the
/// same <see cref="BoundedObservableCollection{T}"/> instance the Diagnostics viewer
/// (Story 5.1) will bind to — no copy, no view layer (AC-8.2).
/// </summary>
public interface IDiagnosticRingSink
{
    /// <summary>
    /// Push an entry. Non-blocking. Resolves <see cref="DiagnosticRow.IdentityLabel"/> +
    /// <see cref="DiagnosticRow.EndpointLabel"/> at arrival (snapshot semantics per FR-041),
    /// then marshals the prepend through <c>IUiDispatcher.Post</c> so the
    /// <see cref="BoundedObservableCollection{T}"/> mutation happens on the UI thread.
    /// </summary>
    void Push(DiagnosticEntry entry);

    /// <summary>
    /// The bounded collection of resolved rows (newest-first, FR-041 cap = 5000).
    /// Story 5.1's <c>DiagnosticsViewModel.Entries</c> binds to this SAME instance.
    /// </summary>
    BoundedObservableCollection<DiagnosticRow> Entries { get; }
}
