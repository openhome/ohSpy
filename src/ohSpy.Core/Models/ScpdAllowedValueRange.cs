namespace ohSpy.Core.Models;

/// <summary>
/// Numeric range constraint on a <see cref="ScpdStateVariable"/>. <see cref="Step"/> is nullable
/// per AC-5.5: SCPD may omit <c>&lt;step&gt;</c>, in which case the value is unconstrained.
/// </summary>
public sealed record ScpdAllowedValueRange(double Minimum, double Maximum, double? Step);
