namespace ohSpy.Core.ViewModels;

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ohSpy.Core.Devices;
using ohSpy.Core.Models;

public partial class ActionNodeViewModel : ObservableObject, INodeViewModel
{
    private readonly ScpdAction _action;
    // Story 3.2: parent context threaded ServiceNode → ActionNode (the same way ServiceNode
    // receives its context from DeviceNode). The popup VM needs LocationUrl + Uuid + DeviceToken,
    // all carried on the RegistryEntry — so we pass the entry rather than 4 scalars.
    private readonly ServiceDescription _parentService;
    private readonly RegistryEntry _parentEntry;
    private readonly NodeServices _services;

    [ObservableProperty] private string _label = "";

    public NodeKind Kind => NodeKind.Action;

    // FR-045 action glyph. U+E943 = Segoe MDL2 Assets "Code" glyph ("callable method").
#pragma warning disable CA1822
    public string KindGlyph => "";
#pragma warning restore CA1822

    // AC-A1.3 / AC-2.6.7: actions are leaves — empty children, no chevron.
    public ObservableCollection<INodeViewModel> Children { get; } = [];

    public ActionNodeViewModel(
        ScpdAction action, ServiceDescription parentService, RegistryEntry parentEntry, NodeServices services)
    {
        _action = action;
        _parentService = parentService;
        _parentEntry = parentEntry;
        _services = services;
        Label = action.Name;
    }

    // AC-3.2.4: double-click an action row opens the invocation popup (FR-025). Crosses the
    // Core/App boundary via the IInvocationPopupLauncher seam (a Core VM can't new a WinUI Window —
    // Pattern 2). Sync fire-and-forget, mirroring DeviceNodeViewModel.OpenProperties.
    [RelayCommand]
    private void OpenInvocationPopup() =>
        _services.InvocationPopupLauncher.Open(_action, _parentService, _parentEntry);

    string INodeViewModel.Label => Label;
}
