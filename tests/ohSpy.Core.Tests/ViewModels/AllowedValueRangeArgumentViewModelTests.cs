namespace ohSpy.Core.Tests.ViewModels;

using System.Globalization;
using FluentAssertions;
using ohSpy.Core.Models;
using ohSpy.Core.ViewModels;

/// <summary>
/// Story 3.3 / FR-103 — <see cref="AllowedValueRangeArgumentViewModel"/> (the numeric range variant).
/// Asserts Min/Max/Step storage, the default pre-population rules (AC-3.3.5), culture-invariant
/// ResolvedValue formatting (AC-3.3.4 #11), and the off-step / out-of-range ValidationError gate
/// (AC-3.3.6).
/// </summary>
public sealed class AllowedValueRangeArgumentViewModelTests
{
    private static ScpdArgument Arg(string name = "DesiredVolume") =>
        new(name, "VolumeVar", ScpdDirection.In);

    [Fact]
    [Trait("ac", "AC-3.3.4")]
    public void MinMaxStep_Stored_FR103()
    {
        var vm = new AllowedValueRangeArgumentViewModel(Arg(), 0, 100, 1, null);

        vm.Minimum.Should().Be(0);
        vm.Maximum.Should().Be(100);
        vm.Step.Should().Be(1);
        vm.StepOrOne.Should().Be(1);
    }

    [Fact]
    [Trait("ac", "AC-3.3.4")]
    public void StepOrOne_DefaultsToOne_WhenStepNull_FR103()
    {
        var vm = new AllowedValueRangeArgumentViewModel(Arg(), -15, 15, null, null);

        vm.Step.Should().BeNull();
        vm.StepOrOne.Should().Be(1, "a null step still nudges the spinner by 1");
    }

    [Fact]
    [Trait("ac", "AC-3.3.5")]
    public void NumericValue_DefaultsToDeclaredDefault_WhenInRangeOnStep_FR103()
    {
        var vm = new AllowedValueRangeArgumentViewModel(Arg(), 0, 100, 1, "50");

        vm.NumericValue.Should().Be(50);
    }

    [Fact]
    [Trait("ac", "AC-3.3.5")]
    public void NumericValue_DefaultsToMin_WhenDefaultOutOfRange_FR103()
    {
        var vm = new AllowedValueRangeArgumentViewModel(Arg(), 0, 100, 1, "150");

        vm.NumericValue.Should().Be(0, "an out-of-range default falls back to Minimum");
    }

    [Fact]
    [Trait("ac", "AC-3.3.5")]
    public void NumericValue_DefaultsToMin_WhenDefaultOffStep_FR103()
    {
        // Range 0..10 step 2 → 3 is off-step → fall back to Minimum.
        var vm = new AllowedValueRangeArgumentViewModel(Arg(), 0, 10, 2, "3");

        vm.NumericValue.Should().Be(0, "an off-step default falls back to Minimum");
    }

    [Fact]
    [Trait("ac", "AC-3.3.5")]
    public void NumericValue_DefaultsToMin_WhenDefaultUnparsable_FR103()
    {
        var vm = new AllowedValueRangeArgumentViewModel(Arg(), 0, 100, 1, "loud");

        vm.NumericValue.Should().Be(0);
    }

    [Fact]
    [Trait("ac", "AC-3.3.4")]
    public void ResolvedValue_FormatsInvariant_FR103()
    {
        var vm = new AllowedValueRangeArgumentViewModel(Arg(), 0, 100, 1, "12");

        vm.ResolvedValue.Should().Be("12");
    }

    [Fact]
    [Trait("ac", "AC-3.3.4")]
    public void ResolvedValue_UsesDotDecimalSeparator_UnderCommaCulture_FR103()
    {
        // FR-103: a comma-decimal culture must NOT corrupt the wire value — invariant '.' is required.
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var vm = new AllowedValueRangeArgumentViewModel(Arg(), 0, 10, 0.5, null) { NumericValue = 2.5 };

            vm.ResolvedValue.Should().Be("2.5", "the wire form must use '.' even under a comma-decimal culture");
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    [Trait("ac", "AC-3.3.6")]
    public void ValidationError_IsNull_WhenOnStepInRange_FR103()
    {
        var vm = new AllowedValueRangeArgumentViewModel(Arg(), 0, 100, 1, "50");

        vm.ValidationError.Should().BeNull();

        vm.NumericValue = 30;

        vm.ValidationError.Should().BeNull("30 is on-step and in range");
    }

    [Fact]
    [Trait("ac", "AC-3.3.6")]
    public void ValidationError_Set_WhenOffStep_FR103()
    {
        var vm = new AllowedValueRangeArgumentViewModel(Arg(), 0, 10, 2, "0");

        vm.NumericValue = 3; // off-step (0,2,4,6,8,10 are valid)

        vm.ValidationError.Should().NotBeNull();
        vm.ValidationError.Should().Contain("multiple of 2");
    }

    [Fact]
    [Trait("ac", "AC-3.3.6")]
    public void ValidationError_Set_WhenOutOfRange_FR103()
    {
        var vm = new AllowedValueRangeArgumentViewModel(Arg(), 0, 100, 1, "0");

        vm.NumericValue = 250;

        vm.ValidationError.Should().NotBeNull();
        vm.ValidationError.Should().Contain("between 0 and 100");
    }

    [Fact]
    [Trait("ac", "AC-3.3.6")]
    public void ValidationError_ClearsAfterCorrection_FR103()
    {
        var vm = new AllowedValueRangeArgumentViewModel(Arg(), 0, 10, 2, "0");

        vm.NumericValue = 3;
        vm.ValidationError.Should().NotBeNull();

        vm.NumericValue = 4;
        vm.ValidationError.Should().BeNull("4 is on-step again");
    }

    [Fact]
    [Trait("ac", "AC-3.3.6")]
    public void Validate_ReassertsValidationError_FR103()
    {
        // Range with no step → only range bounds matter; Validate() is idempotent.
        var vm = new AllowedValueRangeArgumentViewModel(Arg(), 0, 100, null, "50");

        vm.Validate();
        vm.ValidationError.Should().BeNull();
    }

    [Fact]
    [Trait("ac", "AC-3.3.4")]
    public void Name_ComesFromArgument_FR103()
    {
        var vm = new AllowedValueRangeArgumentViewModel(Arg("DesiredVolume"), 0, 100, 1, null);

        vm.Name.Should().Be("DesiredVolume");
    }
}
