namespace ohSpy.Core.Tests.ViewModels;

using FluentAssertions;
using ohSpy.Core.Models;
using ohSpy.Core.ViewModels;

/// <summary>
/// Story 3.2 — <see cref="ArgumentInputViewModel"/> unit tests (AC-3.2.3). The text-only base
/// Story 3.3 extends: Name exposed, Value defaults to "", ResolvedValue funnels to Value.
/// </summary>
public sealed class ArgumentInputViewModelTests
{
    private static ScpdArgument Arg(string name) =>
        new(name, "RelatedVar", ScpdDirection.In);

    [Fact]
    [Trait("ac", "AC-3.2.3")]
    public void Name_FromArgument_AC323()
    {
        var vm = new ArgumentInputViewModel(Arg("Volume"));

        vm.Name.Should().Be("Volume");
    }

    [Fact]
    [Trait("ac", "AC-3.2.3")]
    public void Value_DefaultsToEmptyString_AC323()
    {
        var vm = new ArgumentInputViewModel(Arg("Volume"));

        vm.Value.Should().Be("");
    }

    [Fact]
    [Trait("ac", "AC-3.2.3")]
    public void ResolvedValue_DefaultsToValue_Empty_AC323()
    {
        var vm = new ArgumentInputViewModel(Arg("Volume"));

        // An untouched input resolves to "" → 3.1 builder emits a self-closing <Volume /> (open Q #4).
        vm.ResolvedValue.Should().Be("");
    }

    [Fact]
    [Trait("ac", "AC-3.2.3")]
    public void ResolvedValue_TracksValue_AC323()
    {
        var vm = new ArgumentInputViewModel(Arg("Volume")) { Value = "42" };

        vm.ResolvedValue.Should().Be("42");
    }

    [Fact]
    [Trait("ac", "AC-3.2.3")]
    public void NotSealed_SoStory33CanSubclass_AC323()
    {
        typeof(ArgumentInputViewModel).IsSealed.Should().BeFalse(
            "Story 3.3 subclasses this for allowed-value-list / range variants");
    }
}
