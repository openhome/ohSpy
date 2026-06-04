namespace ohSpy.Core.Tests.Fakes;

using System.Collections.Concurrent;
using ohSpy.Core.Diagnostics;

/// <summary>
/// Thread-safe variant of <see cref="CapturingDiagnosticEmitter"/>. The Story 4.1 callback host
/// emits diagnostics from many concurrent per-connection handler tasks (the flood test drives 50
/// at once), so the backing store must tolerate concurrent writers — a plain <c>List</c> would be
/// corrupted under the race.
/// </summary>
internal sealed class ConcurrentCapturingDiagnosticEmitter : IDiagnosticEmitter
{
    public sealed record Entry(string Severity, string Category, string Message, DiagnosticContext Context);

    private readonly ConcurrentQueue<Entry> _entries = new();

    public IReadOnlyList<Entry> Entries => _entries.ToArray();

    public int CountOf(string category) => _entries.Count(e => e.Category == category);

    public void Verbose(string c, string m, DiagnosticContext ctx = default) => _entries.Enqueue(new("Verbose", c, m, ctx));
    public void Information(string c, string m, DiagnosticContext ctx = default) => _entries.Enqueue(new("Information", c, m, ctx));
    public void Warning(string c, string m, DiagnosticContext ctx = default) => _entries.Enqueue(new("Warning", c, m, ctx));
    public void Error(string c, string m, DiagnosticContext ctx = default) => _entries.Enqueue(new("Error", c, m, ctx));
}
