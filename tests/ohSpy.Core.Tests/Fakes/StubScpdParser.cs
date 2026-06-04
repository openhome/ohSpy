namespace ohSpy.Core.Tests.Fakes;

using System.Runtime.CompilerServices;
using ohSpy.Core.Models;
using ohSpy.Core.Scpd;

/// <summary>
/// Controllable <see cref="IScpdParser"/> for deterministic tests (the real parser's timing is
/// non-deterministic). Set <see cref="Actions"/> for a happy-path stream, or <see cref="Thrower"/>
/// to simulate a streaming parse failure. For Story 3.3's invocation-popup state-table load, set
/// <see cref="StateTable"/> (the canned table <see cref="ReadStateTableAsync"/> returns) or
/// <see cref="StateTableThrower"/> (to simulate a state-table parse failure).
/// </summary>
internal sealed class StubScpdParser : IScpdParser
{
    public IReadOnlyList<ScpdAction> Actions { get; set; } = Array.Empty<ScpdAction>();
    public Func<Exception>? Thrower { get; set; }

    /// <summary>The state table <see cref="ReadStateTableAsync"/> returns. Default: empty.</summary>
    public ScpdStateTable StateTable { get; set; } =
        new(new Dictionary<string, ScpdStateVariable>(StringComparer.Ordinal));

    /// <summary>When set, <see cref="ReadStateTableAsync"/> throws it (simulates a parse failure).</summary>
    public Func<Exception>? StateTableThrower { get; set; }

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

    public Task<ScpdStateTable> ReadStateTableAsync(Stream xml, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (StateTableThrower is not null) throw StateTableThrower();
        return Task.FromResult(StateTable);
    }
}
