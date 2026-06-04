namespace ohSpy.Core.Tests.ViewModels;

using FluentAssertions;
using ohSpy.Core.Models;
using ohSpy.Core.ViewModels;

/// <summary>
/// Story 3.3 / FR-102 — <see cref="AllowedValueListArgumentViewModel"/> (the dropdown variant).
/// Asserts on what the resolution PRODUCED (AllowedValues order, the pre-populated SelectedValue,
/// ResolvedValue), not on inputs handed in (Epic 2 lesson).
/// </summary>
public sealed class AllowedValueListArgumentViewModelTests
{
    private static ScpdArgument Arg(string name = "DesiredMode") =>
        new(name, "ModeVar", ScpdDirection.In);

    // params helper avoids CA1861 (constant array argument) at the call sites.
    private static string[] L(params string[] values) => values;

    [Fact]
    [Trait("ac", "AC-3.3.2")]
    public void AllowedValues_PreservedInDeclaredOrder_FR102()
    {
        var vm = new AllowedValueListArgumentViewModel(Arg(), L("Stereo", "Mono", "Surround"), null);

        vm.AllowedValues.Should().Equal("Stereo", "Mono", "Surround");
    }

    [Fact]
    [Trait("ac", "AC-3.3.2")]
    public void SelectedValue_DefaultsToDeclaredDefault_WhenInList_FR102()
    {
        var vm = new AllowedValueListArgumentViewModel(Arg(), L("Stereo", "Mono", "Surround"), "Mono");

        vm.SelectedValue.Should().Be("Mono");
    }

    [Fact]
    [Trait("ac", "AC-3.3.2")]
    public void SelectedValue_DefaultsToFirst_WhenDefaultNotInList_FR102()
    {
        var vm = new AllowedValueListArgumentViewModel(Arg(), L("Stereo", "Mono"), "Quad");

        vm.SelectedValue.Should().Be("Stereo", "a default that is not a member falls back to the first listed value");
    }

    [Fact]
    [Trait("ac", "AC-3.3.2")]
    public void SelectedValue_DefaultsToFirst_WhenDefaultNull_FR102()
    {
        var vm = new AllowedValueListArgumentViewModel(Arg(), L("Stereo", "Mono"), null);

        vm.SelectedValue.Should().Be("Stereo");
    }

    [Fact]
    [Trait("ac", "AC-3.3.2")]
    public void ResolvedValue_TracksSelectedValue_FR102()
    {
        var vm = new AllowedValueListArgumentViewModel(Arg(), L("Stereo", "Mono", "Surround"), "Stereo");

        vm.ResolvedValue.Should().Be("Stereo");

        vm.SelectedValue = "Surround";

        vm.ResolvedValue.Should().Be("Surround", "ResolvedValue is the single seam the SOAP projection reads");
    }

    [Fact]
    [Trait("ac", "AC-3.3.2")]
    public void Name_ComesFromArgument_FR102()
    {
        var vm = new AllowedValueListArgumentViewModel(Arg("Channel"), L("Master"), null);

        vm.Name.Should().Be("Channel");
    }
}
