namespace ohSpy.Core.Tests.Architecture;

using NetArchTest.Rules;
using ohSpy.Core.Http;   // any type guaranteed to live in ohSpy.Core

/// <summary>
/// Pattern 2 enforcement — ohSpy.Core MUST NOT reference WinUI 3 / WindowsAppSDK /
/// WinRT.Interop types, NOR may it reference ohSpy.App. Four separate facts so test
/// names tell us exactly which rule was violated.
/// </summary>
public sealed class CoreAppBoundaryTests
{
    // Anchor type for assembly resolution — IUpnpHttpClient lives in ohSpy.Core.
    private static System.Reflection.Assembly CoreAssembly => typeof(IUpnpHttpClient).Assembly;

    [Fact]
    [Trait("ac", "AC-6")]
    public void Core_HasNoDependencyOnMicrosoftUi()
    {
        var result = Types.InAssembly(CoreAssembly)
            .Should()
            .NotHaveDependencyOn("Microsoft.UI")
            .GetResult();

        AssertSuccess(result, "Pattern 2: ohSpy.Core MUST NOT reference Microsoft.UI.* (WinUI 3 types).");
    }

    [Fact]
    [Trait("ac", "AC-6")]
    public void Core_HasNoDependencyOnMicrosoftWindows()
    {
        var result = Types.InAssembly(CoreAssembly)
            .Should()
            .NotHaveDependencyOn("Microsoft.Windows")
            .GetResult();

        AssertSuccess(result, "Pattern 2: ohSpy.Core MUST NOT reference Microsoft.Windows.* (WindowsAppSDK types).");
    }

    [Fact]
    [Trait("ac", "AC-6")]
    public void Core_HasNoDependencyOnWinRTInterop()
    {
        var result = Types.InAssembly(CoreAssembly)
            .Should()
            .NotHaveDependencyOn("WinRT.Interop")
            .GetResult();

        AssertSuccess(result, "Pattern 2: ohSpy.Core MUST NOT reference WinRT.Interop.* types.");
    }

    [Fact]
    [Trait("ac", "AC-6")]
    public void Core_HasNoDependencyOnApp()
    {
        var result = Types.InAssembly(CoreAssembly)
            .Should()
            .NotHaveDependencyOn("ohSpy.App")
            .GetResult();

        AssertSuccess(result, "Pattern 2: ohSpy.Core MUST NOT reference ohSpy.App.* (only App references Core).");
    }

    private static void AssertSuccess(TestResult result, string message)
    {
        if (result.IsSuccessful) return;
        var failures = string.Join(System.Environment.NewLine,
            result.FailingTypes?.Select(t => $"  - {t.FullName}") ?? System.Array.Empty<string>());
        Assert.Fail($"{message}\n\nViolating types:\n{failures}");
    }
}
