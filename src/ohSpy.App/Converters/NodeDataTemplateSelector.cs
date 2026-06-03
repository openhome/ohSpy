namespace ohSpy.App.Converters;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ohSpy.Core.ViewModels;

// Selects the correct DataTemplate for heterogeneous tree nodes (FR-045).
// DeviceNodeViewModel gets the full glyph+name+detail template; Service/Action get their
// own glyph+label rows (Story 2.6). All other INodeViewModel types (Loading placeholder,
// InlineError) get the FallbackTemplate.
public sealed class NodeDataTemplateSelector : DataTemplateSelector
{
    public DataTemplate DeviceTemplate { get; set; } = null!;
    public DataTemplate ServiceTemplate { get; set; } = null!;
    public DataTemplate ActionTemplate { get; set; } = null!;
    public DataTemplate FallbackTemplate { get; set; } = null!;

    protected override DataTemplate SelectTemplateCore(object item, DependencyObject container) =>
        item switch
        {
            DeviceNodeViewModel  => DeviceTemplate,
            ServiceNodeViewModel => ServiceTemplate,
            ActionNodeViewModel  => ActionTemplate,
            _                    => FallbackTemplate, // Loading / InlineError
        };
}
