namespace ohSpy.Core.Diagnostics;

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
    string EndpointLabel);
