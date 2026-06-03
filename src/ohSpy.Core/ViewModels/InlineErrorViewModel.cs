namespace ohSpy.Core.ViewModels;

public sealed class InlineErrorViewModel : INodeViewModel
{
    public string Label { get; }
    public NodeKind Kind => NodeKind.Error;
    public InlineErrorViewModel(string message) => Label = message;
}
