namespace ohSpy.Core.Diagnostics;

using System.Globalization;

/// <summary>
/// UI-bound row type for the FR-041 diagnostics viewer. Wraps a <see cref="DiagnosticEntry"/>
/// plus the snapshot-resolved <see cref="IdentityLabel"/> and <see cref="EndpointLabel"/>
/// computed AT THE TIME the row was pushed to the sink — later registry mutations do NOT
/// update existing rows (FR-041 "snapshot at arrival" invariant).
/// </summary>
/// <param name="Entry">The originating diagnostic entry.</param>
/// <param name="IdentityLabel">Resolved per FR-041: friendly name OR <c>"uuid:..."</c> OR <c>"—"</c>.</param>
/// <param name="EndpointLabel">Resolved per FR-041: host[:port] OR <c>RemoteEndpoint</c> OR <c>"—"</c>.</param>
public sealed record DiagnosticRow(
    DiagnosticEntry Entry,
    string IdentityLabel,
    string EndpointLabel)
{
    /// <summary>
    /// FR-041 timestamp display: the entry's UTC timestamp formatted <c>HH:mm:ss.fff</c> (invariant
    /// culture). UTC — NOT local (the epic/FR-041 are explicit; this differs from the SSDP log, which
    /// uses local time). Projected on the row so the App XAML stays a dumb <c>x:Bind</c> to a string,
    /// and so binding never touches the <see cref="DiagnosticContext"/> struct (WinUI struct-binding
    /// trap, memory <c>winui-no-struct-databinding</c>).
    /// </summary>
    public string TimestampDisplay =>
        Entry.TimestampUtc.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);

    /// <summary>The severity as a display string (e.g. <c>"Warning"</c>) — a dumb-XAML projection.</summary>
    public string SeverityLabel => Entry.Severity.ToString();
}
