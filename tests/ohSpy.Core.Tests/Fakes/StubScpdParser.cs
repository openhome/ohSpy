namespace ohSpy.Core.Tests.Fakes;

using System.Runtime.CompilerServices;
using ohSpy.Core.Models;
using ohSpy.Core.Scpd;

/// <summary>
/// Controllable <see cref="IScpdParser"/> for deterministic incremental-emission tests
/// (the real parser's timing is non-deterministic). Set <see cref="Actions"/> for a
/// happy-path stream, or <see cref="Thrower"/> to simulate a parse failure.
/// </summary>
internal sealed class StubScpdParser : IScpdParser
{
    public IReadOnlyList<ScpdAction> Actions { get; set; } = Array.Empty<ScpdAction>();
    public Func<Exception>? Thrower { get; set; }

    public async IAsyncEnumerable<ScpdAction> StreamActionsAsync(
        Stream xml, [EnumeratorCancellation] CancellationToken ct)
    {
        if (Thrower is not null) throw Thrower();
        foreach (var a in Actions)
        {
            ct.ThrowIfCancellationRequested();
            yield return a;
            await Task.Yield();
        }
    }

    public Task<ScpdStateTable> ReadStateTableAsync(Stream xml, CancellationToken ct) =>
        throw new NotSupportedException();
}
