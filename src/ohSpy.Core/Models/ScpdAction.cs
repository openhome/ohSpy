namespace ohSpy.Core.Models;

/// <summary>
/// A single SCPD action — name + ordered input and output argument lists.
/// Yielded one at a time by <c>IScpdParser.StreamActionsAsync</c> (FR-100 incremental parse).
/// </summary>
public sealed record ScpdAction(
    string Name,
    IReadOnlyList<ScpdArgument> Inputs,
    IReadOnlyList<ScpdArgument> Outputs);
