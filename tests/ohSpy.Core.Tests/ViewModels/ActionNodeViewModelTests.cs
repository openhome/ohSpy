namespace ohSpy.Core.Tests.ViewModels;

using FluentAssertions;
using ohSpy.Core.Models;
using ohSpy.Core.ViewModels;

/// <summary>
/// Story 2.6 — <see cref="ActionNodeViewModel"/> unit tests (AC-2.6.7 leaf shape).
/// </summary>
public sealed class ActionNodeViewModelTests
{
    private static ScpdAction Action(string name) =>
        new(name, Array.Empty<ScpdArgument>(), Array.Empty<ScpdArgument>());

    [Fact]
    [Trait("ac", "AC-2.6.7")]
    public void Constructor_LabelIsActionName_AC267()
    {
        var vm = new ActionNodeViewModel(Action("Play"));

        vm.Label.Should().Be("Play");
    }

    [Fact]
    [Trait("ac", "AC-2.6.7")]
    public void Kind_IsAction_AC267()
    {
        var vm = new ActionNodeViewModel(Action("Play"));

        vm.Kind.Should().Be(NodeKind.Action);
    }

    [Fact]
    [Trait("ac", "AC-A1.3")]
    public void Children_IsEmpty_ACA13()
    {
        var vm = new ActionNodeViewModel(Action("Play"));

        vm.Children.Count.Should().Be(0); // leaf; no placeholder → XAML renders no chevron
    }
}
