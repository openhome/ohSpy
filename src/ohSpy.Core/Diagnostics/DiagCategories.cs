namespace ohSpy.Core.Diagnostics;

/// <summary>
/// Single source of truth for diagnostic category strings. Each constant carries the
/// mandatory <see cref="DiagnosticContext"/> fields per Pattern 11.
/// </summary>
public static class DiagCategories
{
    /// <summary>Mandatory context: Url, Elapsed, Budget.</summary>
    public const string HttpTimeout = "Http.Timeout";

    /// <summary>Mandatory context: Url; StatusCode if present.</summary>
    public const string HttpTransport = "Http.Transport";

    /// <summary>Mandatory context: Url.</summary>
    public const string HttpOversizeBody = "Http.OversizeBody";
}
