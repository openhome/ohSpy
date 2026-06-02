namespace ohSpy.Core.Diagnostics;

/// <summary>
/// On-disk rolling log sink. Writes JSON-lines to <c>%LOCALAPPDATA%\ohSpy\diagnostics\</c>;
/// rotates at 2 MB; retains ≤ 8 files (total ≤ 16 MB).
/// <para>
/// <see cref="Push"/> is non-blocking — entries enqueue to a <c>Channel&lt;T&gt;</c>
/// (capacity 1000, FullMode=DropOldest); a background pump task drains to disk.
/// </para>
/// <para>
/// On startup failure (unwritable directory / file), the sink emits ONE warning via the
/// ring sink (<see cref="DiagCategories.DiagnosticsFileSinkUnavailable"/>) and silently
/// no-ops on subsequent <see cref="Push"/> calls. App start MUST NOT block on this
/// (FR-042, AC-8.6).
/// </para>
/// </summary>
public interface IDiagnosticFileSink : IAsyncDisposable
{
    /// <summary>Non-blocking enqueue. O(1) channel write. No exceptions reach the caller.</summary>
    void Push(DiagnosticEntry entry);

    /// <summary>
    /// Drain the channel synchronously (5 s budget) and close the file handle.
    /// Called from <see cref="IAsyncDisposable.DisposeAsync"/> AND can be called
    /// explicitly during App shutdown.
    /// </summary>
    Task FlushAsync(CancellationToken ct);
}
