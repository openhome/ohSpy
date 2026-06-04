namespace ohSpy.Core.Models;

using System.Linq;

/// <summary>
/// One rendered property of an <see cref="EventNotification"/> — a reference-type (name, value)
/// pair. Story 4.3's event list binds <see cref="EventNotification.PropertyRows"/> via typed
/// <c>x:Bind</c>; binding the raw <c>IReadOnlyDictionary</c> would surface <c>KeyValuePair</c>
/// STRUCT items, and WinUI 3 classic <c>{Binding Key}</c>/<c>{Binding Value}</c> against a value-type
/// DataContext access-violates the XAML layer (smoke crash 2026-06-04). Reference type → safe x:Bind.
/// </summary>
public sealed record EventProperty(string Name, string Value);

/// <summary>
/// A single parsed GENA <c>NOTIFY</c> event (Story 4.2, FR-104; epic L1596-1598). This is the
/// boundary where the raw <c>byte[]</c> from the Story 4.1 callback host has been parsed into a
/// property dictionary: 4.1 ships raw bytes, <b>4.2 owns the <c>&lt;e:propertyset&gt;</c> parse</b>,
/// and Story 4.3 only renders this record (it does NOT re-parse XML).
/// <para>
/// Placement reconciliation: the epic AC pins <c>ohSpy.Core/Models/EventNotification.cs</c> while the
/// architecture source tree (L2112) sketched it under <c>Events/</c>. It lives in <c>Models/</c>
/// (epic AC wins; consistent with the <c>SoapArgument</c>/<c>ServiceDescription</c> data-record
/// placement). <c>BoundedObservableCollection&lt;EventNotification&gt;</c> is what 4.3 binds.
/// </para>
/// </summary>
/// <param name="Sid">The subscription identifier this event was routed to (the live SID).</param>
/// <param name="Seq">The GENA event sequence number from the <c>SEQ</c> header (0 if absent — initial event).</param>
/// <param name="ReceivedUtc">The host's UTC arrival timestamp (carried verbatim from the <see cref="ohSpy.Core.Events.NotifyRequest"/>).</param>
/// <param name="Properties">The parsed <c>&lt;e:propertyset&gt;</c>: each inner property element name → its text value.</param>
public sealed record EventNotification(
    string Sid, long Seq, DateTime ReceivedUtc, IReadOnlyDictionary<string, string> Properties)
{
    /// <summary>
    /// The <see cref="Properties"/> as an ordered list of reference-type <see cref="EventProperty"/>
    /// rows — the shape Story 4.3's event-list DataTemplate binds via typed <c>x:Bind</c> (see the
    /// <see cref="EventProperty"/> remarks for why the raw dictionary cannot be bound directly).
    /// </summary>
    public IReadOnlyList<EventProperty> PropertyRows =>
        Properties.Select(p => new EventProperty(p.Key, p.Value)).ToList();
}
