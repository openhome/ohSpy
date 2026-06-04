namespace ohSpy.App.Converters;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ohSpy.Core.ViewModels;

// Story 3.3 / FR-102+FR-103 — selects the input control per heterogeneous argument variant.
// An ItemsControl renders one template PER item and the variant differs per row, so a
// DataTemplateSelector keyed on the VM runtime subtype is the idiomatic mechanism (mirrors
// NodeDataTemplateSelector). This is a DELIBERATE divergence from Story 3.2's result-area
// code-behind Visibility projections (which toggle one-of-three SINGLETON panels) — justified
// here by "a list of heterogeneous items" vs "one-of-three singleton panels".
//   AllowedValueListArgumentViewModel  → ComboBox (List)
//   AllowedValueRangeArgumentViewModel → NumberBox (Range)
//   ArgumentInputViewModel (base)      → TextBox  (Text, the 3.2 fallback)
public sealed class ArgumentInputTemplateSelector : DataTemplateSelector
{
    public DataTemplate TextTemplate { get; set; } = null!;
    public DataTemplate ListTemplate { get; set; } = null!;
    public DataTemplate RangeTemplate { get; set; } = null!;

    protected override DataTemplate SelectTemplateCore(object item, DependencyObject container) =>
        item switch
        {
            AllowedValueListArgumentViewModel  => ListTemplate,
            AllowedValueRangeArgumentViewModel => RangeTemplate,
            _                                  => TextTemplate, // base ArgumentInputViewModel
        };

    protected override DataTemplate SelectTemplateCore(object item) =>
        SelectTemplateCore(item, null!);
}
