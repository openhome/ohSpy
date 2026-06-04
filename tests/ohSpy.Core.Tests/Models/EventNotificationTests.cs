namespace ohSpy.Core.Tests.Models;

using FluentAssertions;
using ohSpy.Core.Models;

/// <summary>
/// <see cref="EventNotification.PropertyRows"/> — the typed projection Story 4.3's event list binds.
/// Regression for the smoke crash (2026-06-04): the popup originally bound the raw
/// <c>IReadOnlyDictionary</c>, which surfaces <c>KeyValuePair</c> STRUCT items, and WinUI 3 classic
/// <c>{Binding Key}</c>/<c>{Binding Value}</c> against a value-type DataContext access-violates the
/// XAML layer. Projecting to a reference-type <see cref="EventProperty"/> list is the fix.
/// </summary>
public sealed class EventNotificationTests
{
    private static EventNotification Notify(params (string Key, string Value)[] props) =>
        new("uuid:sid", 1, DateTime.UtcNow, props.ToDictionary(p => p.Key, p => p.Value));

    [Fact]
    public void PropertyRows_ProjectsEachPropertyToTypedReferenceRow()
    {
        var n = Notify(("TransportState", "Playing"), ("Volume", "42"));

        n.PropertyRows.Should().BeEquivalentTo(new[]
        {
            new EventProperty("TransportState", "Playing"),
            new EventProperty("Volume", "42"),
        });
        // Reference type (record class), NOT a KeyValuePair struct — the binding-safety guarantee.
        n.PropertyRows.Should().AllBeOfType<EventProperty>();
    }

    [Fact]
    public void PropertyRows_EmptyProperties_IsEmpty()
    {
        Notify().PropertyRows.Should().BeEmpty();
    }
}
