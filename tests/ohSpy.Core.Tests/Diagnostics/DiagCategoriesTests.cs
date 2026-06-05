namespace ohSpy.Core.Tests.Diagnostics;

using System.Reflection;
using FluentAssertions;
using ohSpy.Core.Diagnostics;

/// <summary>
/// AC-1 — verifies the <see cref="DiagCategories"/> constants surface. The "exact set"
/// test is the strongest guard: any add OR delete fails, forcing a deliberate
/// architecture-spec sync rather than silent drift.
/// </summary>
public class DiagCategoriesTests
{
    private static FieldInfo[] GetConstantFields() =>
        typeof(DiagCategories)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(string))
            .ToArray();

    [Fact]
    [Trait("ac", "AC-1")]
    public void DiagCategories_AllConstantsAreUniqueAndNonEmpty()
    {
        var values = GetConstantFields().Select(f => (string)f.GetRawConstantValue()!).ToArray();

        values.Should().OnlyHaveUniqueItems();
        values.Should().NotContainNulls();
        values.Should().NotContain(string.Empty);
        values.Should().AllSatisfy(v => v.Should().NotBeNullOrWhiteSpace());
    }

    [Fact]
    [Trait("ac", "AC-1")]
    public void DiagCategories_ExactSetMatchesArchitecturePinnedList()
    {
        // Canonical full set of constant NAMES (not values) after Story 1.5. Any add OR
        // delete fails the test — forces a deliberate sync with architecture.md §Decision-8
        // and the epic/story spec rather than silent drift.
        var expectedNames = new[]
        {
            // HTTP
            "HttpTimeout", "HttpTransport", "HttpOversizeBody",
            // SSDP
            "SsdpParse", "SsdpSearchObserved", "SsdpChannelNearFull", "SsdpChannelOverflow",
            // Description
            "DescriptionFetch", "DescriptionFetchMismatch", "DescriptionParse",
            // SCPD
            "ScpdFetch", "ScpdParse",
            // SOAP
            "SoapInvoke", "SoapFault",
            // GENA outbound
            "GenaSubscribe", "GenaSubscribeFailed",
            "GenaUnsubscribe", "GenaUnsubscribeFailed",
            "GenaRenewFailed",
            // GENA inbound
            "GenaCallbackMalformed", "GenaCallbackOversize", "GenaCallbackNoLength",
            "GenaCallbackHeadersTo", "GenaCallbackBodyTo", "GenaCallbackFlood",
            "GenaNotifyReceived",
            // Adapter
            "AdapterSwitch", "AdapterSwitchTimeout",
            // Diagnostics infrastructure
            "DiagnosticsFileSinkUnavailable",
            // XML viewing / shell-open (Story 2.8)
            "ShellExecute", "FeatureNotImplemented",
        };

        var actualNames = GetConstantFields().Select(f => f.Name).ToArray();

        actualNames.Should().BeEquivalentTo(expectedNames,
            "the DiagCategories surface is architecturally pinned — adds/removes must update both arch spec AND this test together");
    }

    [Theory]
    [Trait("ac", "AC-1")]
    [InlineData("HttpTimeout", "Http.Timeout")]
    [InlineData("HttpTransport", "Http.Transport")]
    [InlineData("HttpOversizeBody", "Http.OversizeBody")]
    [InlineData("ScpdParse", "Scpd.Parse")]
    [InlineData("DescriptionParse", "Description.Parse")]
    public void DiagCategories_StoryEarlierConstants_KeepTheirExactValues(string constantName, string expectedValue)
    {
        // Regression guard against accidental rename of constants Story 1.3/1.4 already
        // ship. If anyone changes "Http.Timeout" to "Http.Timeout.V2" or similar, callers
        // already in Stories 1.3 / 1.4 keep working because they reference the constant —
        // but downstream parsers / dashboards that consume the JSON-lines string value
        // would silently break.
        var field = GetConstantFields().Single(f => f.Name == constantName);
        var value = (string)field.GetRawConstantValue()!;
        value.Should().Be(expectedValue);
    }
}
