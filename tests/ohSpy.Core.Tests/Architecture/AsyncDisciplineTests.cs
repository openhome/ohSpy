namespace ohSpy.Core.Tests.Architecture;

using System.Reflection;
using ohSpy.Core.Http;

/// <summary>
/// Pattern 6 enforcement — no <c>.Result</c> / <c>.Wait()</c> / <c>.GetAwaiter().GetResult()</c>
/// in ohSpy.Core. The PRIMARY mechanism is the Microsoft.VisualStudio.Threading.Analyzers
/// build-time lint (VSTHRD002 / 003 / 100). AC-13.3 demands that adding <c>.Wait()</c> to
/// any Core async call site causes the pre-commit hook to fail via that analyzer.
/// <para>
/// NetArchTest 1.x performs type-level dependency analysis, NOT method-call-site analysis.
/// It cannot directly detect "this method body invokes .Result" — that requires IL scanning
/// (Mono.Cecil) or a Roslyn analyzer. The architecture lists "Roslyn analyzer" as a deferred
/// follow-up; this test is a placeholder that gives the rule a flagged spot in the suite,
/// with the AC traits for filterability.
/// </para>
/// </summary>
public sealed class AsyncDisciplineTests
{
    private static Assembly CoreAssembly => typeof(IUpnpHttpClient).Assembly;

    // Pattern 6 enforcement IS handled — at Core's compile time, via the VSTHRD002 /
    // 003 / 100 analyzers wired in Directory.Build.props. This skipped test gives the
    // rule a place in the architecture-test suite (with the AC traits for filterability)
    // and a clear TODO for the eventual Roslyn analyzer (architecture line 2028 open
    // follow-up). Using `Skip` not `Assert.True(true)` because the latter is an anti-
    // pattern (always-passes, no signal); Skip is the canonical xUnit shape for
    // "intentional placeholder, deferred enforcement, see comment."
    [Fact(Skip = "Pattern 6 enforced by VSTHRD002/003/100 build-time analyzers. " +
                 "AC-13.3 manual regression: add .Wait() to Core, observe build break. " +
                 "This placeholder will host a Roslyn-analyzer-based assertion when the " +
                 "D8/Pattern-11 follow-up (architecture line 2028) ships.")]
    [Trait("ac", "AC-7")]
    [Trait("ac", "AC-13.3")]
    public void Core_AsyncDiscipline_NoBlockingWaits()
    {
        // Body intentionally empty — xUnit skips the test based on the Fact's Skip
        // attribute. Filter via `dotnet test --filter "ac=AC-7"` shows the test as
        // Skipped, not Passed-Trivially.
        _ = CoreAssembly;
    }

    // The mechanism we CAN test: scan IL via Mono.Cecil. But Mono.Cecil isn't in our
    // dependency graph and adding a package just for this test is heavy. If a future
    // change introduces a real risk (e.g., a Core type uses dynamic invocation to call
    // .Result, bypassing the analyzer), upgrade this to an IL scan.
}
