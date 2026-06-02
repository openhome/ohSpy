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

    // Story 1.4 — pre-declared for downstream consumers. The parsers themselves do NOT
    // emit diagnostics (no URL context); Stories 2.3 / 2.6 catch UpnpProtocolException +
    // re-emit with the real URL.

    /// <summary>Mandatory context: Url; ErrorText for the wrapped XmlException message.</summary>
    public const string ScpdParse = "Scpd.Parse";

    /// <summary>Mandatory context: DeviceUuid, Url; ErrorText for the wrapped XmlException message.</summary>
    public const string DescriptionParse = "Description.Parse";
}
