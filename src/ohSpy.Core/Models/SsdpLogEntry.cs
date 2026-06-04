namespace ohSpy.Core.Models;

using System.Globalization;

/// <summary>
/// One row in the SSDP log right pane (FR-003 / FR-014 / FR-015). Immutable snapshot —
/// stamped at receipt and never mutated, so the bound row template uses OneTime x:Bind
/// (no INotifyPropertyChanged needed). Newest-first; capped at 10,000 via
/// BoundedObservableCollection (FR-016).
/// </summary>
public sealed record SsdpLogEntry(
    DateTime TimestampUtc,
    SsdpLogKind Kind,
    string Udn)
{
    /// <summary>Uppercase literal token for the row (AC-2.7.4): "ALIVE" / "BYEBYE".</summary>
    public string KindToken => Kind == SsdpLogKind.Alive ? "ALIVE" : "BYEBYE";

    /// <summary>The UDN string for the row (already carries the <c>uuid:</c> prefix; Amendment A30).</summary>
    public string UdnText => Udn;

    /// <summary>Local-time HH:mm:ss.fff for the operator (the wire stamp is UTC).</summary>
    public string TimestampDisplay =>
        TimestampUtc.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
}
