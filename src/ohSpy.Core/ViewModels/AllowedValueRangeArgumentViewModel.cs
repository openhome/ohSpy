namespace ohSpy.Core.ViewModels;

using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using ohSpy.Core.Models;

/// <summary>
/// Story 3.3 / FR-103 — the <c>&lt;allowedValueRange&gt;</c> numeric variant of
/// <see cref="ArgumentInputViewModel"/>. Resolved by <c>InvocationPopupViewModel.InitializeAsync</c>
/// when the argument's related state variable declares an <c>&lt;allowedValueRange&gt;</c> on a
/// numeric <c>&lt;dataType&gt;</c> with a coherent min/max/step. The App renders it as a WinUI
/// <c>NumberBox</c> bounded to <see cref="Minimum"/>/<see cref="Maximum"/> with the spinner stepping
/// by <see cref="Step"/>.
/// <para>
/// <see cref="ResolvedValue"/> is formatted with <see cref="CultureInfo.InvariantCulture"/> (FR-103):
/// the UPnP wire form requires a <c>'.'</c> decimal separator — a current-culture <c>ToString()</c>
/// (e.g. <c>de-DE</c>'s comma) would corrupt the value.
/// </para>
/// </summary>
public sealed partial class AllowedValueRangeArgumentViewModel : ArgumentInputViewModel
{
    /// <summary>Tolerance for the off-step / boundary float comparison (XML-parsed doubles).</summary>
    private const double Epsilon = 1e-9;

    public double Minimum { get; }
    public double Maximum { get; }

    /// <summary>The declared <c>&lt;step&gt;</c>, or null when omitted (value unconstrained beyond min/max).</summary>
    public double? Step { get; }

    /// <summary><see cref="Step"/> defaulted to 1 — drives the App NumberBox spinner's <c>SmallChange</c>
    /// (which needs a non-null double). A null step means no step constraint; the spinner still nudges by 1.</summary>
    public double StepOrOne => Step ?? 1;

    /// <summary>The current numeric value (bound TwoWay to the NumberBox).</summary>
    [ObservableProperty] private double _numericValue;

    /// <summary>Inline validation message; null when the current value is valid (AC-3.3.6).</summary>
    [ObservableProperty] private string? _validationError;

    /// <summary>
    /// Pre-populates <see cref="NumericValue"/> per AC-3.3.5: the state variable's
    /// <c>&lt;defaultValue&gt;</c> (parsed invariant) when it satisfies the range and step, otherwise
    /// <paramref name="min"/>.
    /// </summary>
    public AllowedValueRangeArgumentViewModel(
        ScpdArgument argument, double min, double max, double? step, string? defaultValue)
        : base(argument)
    {
        Minimum = min;
        Maximum = max;
        Step = step;

        _numericValue =
            defaultValue is not null
            && double.TryParse(defaultValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            && Satisfies(d)
                ? d
                : min;

        // Seed the validation state for the initial value (always valid here, but keeps the field honest).
        _validationError = ComputeValidationError(_numericValue);
    }

    /// <summary>FR-103: the wire value is the culture-invariant string form of the numeric value.</summary>
    public override string ResolvedValue => NumericValue.ToString(CultureInfo.InvariantCulture);

    // Re-validate whenever the operator edits the value (AC-3.3.6).
    partial void OnNumericValueChanged(double value) => ValidationError = ComputeValidationError(value);

    /// <summary>
    /// Re-runs validation against the current <see cref="NumericValue"/>; the popup VM calls this as a
    /// pre-flight before building the SOAP request so an off-step value can never go on the wire.
    /// </summary>
    public void Validate() => ValidationError = ComputeValidationError(NumericValue);

    private string? ComputeValidationError(double value)
    {
        if (value < Minimum - Epsilon || value > Maximum + Epsilon)
            return $"Value must be between {Minimum.ToString(CultureInfo.InvariantCulture)} and {Maximum.ToString(CultureInfo.InvariantCulture)}";

        if (Step is > 0 && !IsOnStep(value))
            return $"Value must be a multiple of {Step.Value.ToString(CultureInfo.InvariantCulture)} from {Minimum.ToString(CultureInfo.InvariantCulture)}";

        return null;
    }

    // Whole-of-range satisfaction (used for default pre-population): in [min,max] AND on-step.
    private bool Satisfies(double value) =>
        value >= Minimum - Epsilon
        && value <= Maximum + Epsilon
        && (Step is not > 0 || IsOnStep(value));

    private bool IsOnStep(double value)
    {
        var s = Step!.Value;
        var n = Math.Round((value - Minimum) / s);
        if (n < 0) return false;
        var tolerance = Epsilon * Math.Max(1, Math.Abs(value));
        return Math.Abs(Minimum + n * s - value) <= tolerance;
    }
}
