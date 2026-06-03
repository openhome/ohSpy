namespace ohSpy.Core.ViewModels;

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ohSpy.Core.Models;

public partial class ActionNodeViewModel : ObservableObject, INodeViewModel
{
    private readonly ScpdAction _action;

    [ObservableProperty] private string _label = "";

    public NodeKind Kind => NodeKind.Action;

    // FR-045 action glyph. U+E943 = Segoe MDL2 Assets "Code" glyph ("callable method").
#pragma warning disable CA1822
    public string KindGlyph => "";
#pragma warning restore CA1822

    // AC-A1.3 / AC-2.6.7: actions are leaves — empty children, no chevron.
    public ObservableCollection<INodeViewModel> Children { get; } = [];

    public ActionNodeViewModel(ScpdAction action)
    {
        _action = action;
        Label = action.Name;
    }

    string INodeViewModel.Label => Label;
}
