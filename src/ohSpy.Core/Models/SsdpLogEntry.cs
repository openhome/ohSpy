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
    Guid Uuid)
{
    /// <summary>Uppercase literal token for the row (AC-2.7.4): "ALIVE" / "BYEBYE".</summary>
    public string KindToken => Kind == SsdpLogKind.Alive ? "ALIVE" : "BYEBYE";

    /// <summary>The UUID as a string for the row (bind a string, not a Guid, to TextBlock.Text).</summary>
    public string UuidText => Uuid.ToString();

    /// <summary>Local-time HH:mm:ss.fff for the operator (the wire stamp is UTC).</summary>
    public string TimestampDisplay =>
        TimestampUtc.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
}
