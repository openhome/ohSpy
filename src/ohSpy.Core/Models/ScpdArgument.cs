namespace ohSpy.Core.Models;

/// <summary>
/// A single argument on a SCPD action. <see cref="RelatedStateVariable"/> links back into
/// <see cref="ScpdStateTable"/> for type / constraint lookup (used by FR-102 / FR-103 invocation popup).
/// </summary>
public sealed record ScpdArgument(
    string Name,
    string RelatedStateVariable,
    ScpdDirection Direction);
