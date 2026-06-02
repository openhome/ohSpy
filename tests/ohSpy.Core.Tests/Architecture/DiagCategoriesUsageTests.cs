namespace ohSpy.Core.Tests.Architecture;

using System.Reflection;
using FluentAssertions;
using ohSpy.Core.Diagnostics;

/// <summary>
/// Pattern 11 / D8 — every <see cref="DiagCategories"/> constant is non-empty,
/// dot-separated, and unique. The "every emit call site references DiagCategories.&lt;Name&gt;
/// rather than an inline string literal" rule is enforced via code review + the eventual
/// Roslyn analyzer (architecture open follow-up, line 2028) — NetArchTest 1.x cannot
/// inspect method bodies.
/// </summary>
public sealed class DiagCategoriesUsageTests
{
    private static readonly FieldInfo[] CategoryFields = typeof(DiagCategories)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(string))
        .ToArray();

    [Fact]
    [Trait("ac", "AC-8")]
    public void EveryCategoryConstant_IsNonEmpty()
    {
        var emptyOrNull = CategoryFields
            .Select(f => (Name: f.Name, Value: (string?)f.GetRawConstantValue()))
            .Where(t => string.IsNullOrEmpty(t.Value))
            .ToArray();

        emptyOrNull.Should().BeEmpty(
            "Pattern 11 / D8: every DiagCategories.* constant must be non-empty. " +
            "Empty constants would let inline-string emitters pass undetected.");
    }

    [Fact]
    [Trait("ac", "AC-8")]
    public void EveryCategoryConstant_IsDotSeparated()
    {
        // Per Pattern 11 (architecture line 1906) and D8 (line 994-1030), categories
        // are dot-separated namespaces: Foo.Bar or Foo.Bar.Baz. Catches stray inline
        // additions that don't follow the convention.
        var malformed = CategoryFields
            .Select(f => (Name: f.Name, Value: (string)f.GetRawConstantValue()!))
            .Where(t => !t.Value.Contains('.') || t.Value.StartsWith('.') || t.Value.EndsWith('.'))
            .ToArray();

        malformed.Should().BeEmpty(
            "Pattern 11: every DiagCategories.* constant must be dot-separated (e.g. 'Http.Timeout').");
    }

    [Fact]
    [Trait("ac", "AC-8")]
    public void EveryCategoryConstant_IsUnique()
    {
        var duplicates = CategoryFields
            .Select(f => (string)f.GetRawConstantValue()!)
            .GroupBy(v => v, System.StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToArray();

        duplicates.Should().BeEmpty(
            "Pattern 11: DiagCategories constants must be unique. Duplicate values defeat " +
            "diagnostic-stream filtering by category.");
    }

    // The TRUE AC-8 enforcement — "every emit call site references DiagCategories.<Name>
    // rather than an inline string literal" — requires call-site analysis. NetArchTest
    // 1.x cannot do this; it works on type-level dependencies, not method bodies.
    // Architecture line 2028 lists "Roslyn analyzer" as the open follow-up. Until then:
    // the three structural tests above catch malformed constants, and code review
    // catches inline-string violations. As of Story 1.6 commit, every emitter call
    // site in ohSpy.Core and ohSpy.App uses DiagCategories.* — verified manually.
    [Fact(Skip = "AC-8 call-site discipline currently enforced via code review + the " +
                 "structural tests above. Roslyn analyzer is the long-term answer " +
                 "(architecture line 2028 open follow-up).")]
    [Trait("ac", "AC-8")]
    public void EmitCallSites_UseConstants_NotInlineStrings()
    {
        // Body intentionally empty — skipped test placeholder.
    }
}
