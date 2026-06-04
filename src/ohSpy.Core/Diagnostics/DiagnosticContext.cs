namespace ohSpy.Core.Diagnostics;

/// <summary>
/// Structured context attached to a <see cref="IDiagnosticEmitter"/> call. Zero-allocation
/// when default; all fields nullable so a caller can populate only the relevant ones.
/// </summary>
public readonly record struct DiagnosticContext
{
    public string? DeviceUuid { get; init; }     // FR-041 Identity column (the UDN string; Amendment A30)
    public string? Url { get; init; }            // FR-041 Endpoint column
    public string? RemoteEndpoint { get; init; } // FR-041 Endpoint fallback
    public string? ServiceId { get; init; }
    public string? ActionName { get; init; }
    public int? StatusCode { get; init; }
    public TimeSpan? Elapsed { get; init; }
    public TimeSpan? Budget { get; init; }
    public string? ErrorText { get; init; }
    public string? Sid { get; init; }
}
