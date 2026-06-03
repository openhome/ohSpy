namespace ohSpy.Core.ViewModels;

public sealed class LoadingPlaceholderViewModel : INodeViewModel
{
    public string Label => "Loading…"; // U+2026 ellipsis
    public NodeKind Kind => NodeKind.Placeholder;
}
