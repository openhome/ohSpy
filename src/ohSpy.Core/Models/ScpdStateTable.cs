namespace ohSpy.Core.Models;

/// <summary>
/// Table of all SCPD state variables, indexed by name for O(1) lookup. Returned by
/// <c>IScpdParser.ReadStateTableAsync</c> on demand (lazy — only fetched when the
/// invocation popup needs to resolve a <see cref="ScpdArgument.RelatedStateVariable"/>).
/// </summary>
public sealed record ScpdStateTable(
    IReadOnlyDictionary<string, ScpdStateVariable> ByName);
