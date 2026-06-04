namespace ohSpy.Core.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using ohSpy.Core.Models;

/// <summary>
/// Story 3.3 / FR-102 — the <c>&lt;allowedValueList&gt;</c> dropdown variant of
/// <see cref="ArgumentInputViewModel"/>. Resolved by <c>InvocationPopupViewModel.InitializeAsync</c>
/// when the argument's related state variable declares a non-empty <c>&lt;allowedValueList&gt;</c>.
/// The App renders it as a <c>ComboBox</c> (heterogeneous-input <c>DataTemplateSelector</c>) bound to
/// <see cref="AllowedValues"/> / <see cref="SelectedValue"/>.
/// <para>
/// Sealed subclass (the documented 3.3 shape): the popup VM reads only the overridden
/// <see cref="ResolvedValue"/>, so the SOAP projection (<c>i.ResolvedValue</c>) is unchanged from 3.2.
/// </para>
/// </summary>
public sealed partial class AllowedValueListArgumentViewModel : ArgumentInputViewModel
{
    /// <summary>The allowed values, in SCPD-declared order. Guaranteed non-empty by the caller
    /// (an empty <c>&lt;allowedValueList&gt;</c> falls back to the free-form text base — AC-3.3.3).</summary>
    public IReadOnlyList<string> AllowedValues { get; }

    /// <summary>The operator's current selection (the dropdown's <c>SelectedItem</c>).</summary>
    [ObservableProperty] private string _selectedValue;

    /// <summary>
    /// Pre-populates <see cref="SelectedValue"/> per AC-3.3.2 #7: the state variable's
    /// <c>&lt;defaultValue&gt;</c> when it is a member of <paramref name="allowedValues"/>, otherwise
    /// the first listed value.
    /// </summary>
    public AllowedValueListArgumentViewModel(
        ScpdArgument argument, IReadOnlyList<string> allowedValues, string? defaultValue)
        : base(argument)
    {
        AllowedValues = allowedValues;
        _selectedValue = defaultValue is not null && allowedValues.Contains(defaultValue)
            ? defaultValue
            : allowedValues[0];
    }

    /// <summary>FR-102: the wire value is the selected list item (no free-form text).</summary>
    public override string ResolvedValue => SelectedValue;
}
