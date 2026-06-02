namespace ohSpy.Core.Models;

/// <summary>
/// A SCPD state variable — type plus optional default value and value constraints. Consumed
/// by FR-102 (allowedValueList → dropdown) and FR-103 (allowedValueRange → numeric spinner).
/// </summary>
public sealed record ScpdStateVariable(
    string Name,
    string DataType,
    string? DefaultValue,
    IReadOnlyList<string>? AllowedValueList,
    ScpdAllowedValueRange? AllowedValueRange);
