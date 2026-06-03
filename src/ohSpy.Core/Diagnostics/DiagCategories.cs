namespace ohSpy.Core.Diagnostics;

/// <summary>
/// Single source of truth for diagnostic category strings. Each constant carries the
/// mandatory <see cref="DiagnosticContext"/> fields per Pattern 11. Downstream stories
/// add new constants alongside their new error paths (one PR adds the constant + the
/// call sites; no inline string literals at call sites).
/// </summary>
public static class DiagCategories
{
    // ─── HTTP (Story 1.3) ──────────────────────────────────────────
    /// <summary>Mandatory context: Url, Elapsed, Budget.</summary>
    public const string HttpTimeout = "Http.Timeout";

    /// <summary>Mandatory context: Url; StatusCode if present.</summary>
    public const string HttpTransport = "Http.Transport";

    /// <summary>Mandatory context: Url.</summary>
    public const string HttpOversizeBody = "Http.OversizeBody";

    // ─── SSDP (Story 2.1 / 2.4 — pre-added) ────────────────────────
    /// <summary>Mandatory context: RemoteEndpoint.</summary>
    public const string SsdpParse = "Ssdp.Parse";

    /// <summary>Mandatory context: (none beyond message).</summary>
    public const string SsdpChannelNearFull = "Ssdp.Channel.NearFull";

    /// <summary>Mandatory context: (none beyond message).</summary>
    public const string SsdpChannelOverflow = "Ssdp.Channel.Overflow";

    // ─── Description fetch + parse (Stories 1.4 / 2.3) ─────────────
    /// <summary>Mandatory context: DeviceUuid, Url.</summary>
    public const string DescriptionFetch = "Description.Fetch";

    /// <summary>Mandatory context: DeviceUuid, Url, ErrorText (declared UUID mismatch).</summary>
    public const string DescriptionFetchMismatch = "Description.Fetch.MismatchedRoot";

    /// <summary>Mandatory context: DeviceUuid, Url; ErrorText for the wrapped XmlException message.</summary>
    public const string DescriptionParse = "Description.Parse";

    // ─── SCPD fetch + parse (Story 1.4) ────────────────────────────
    /// <summary>Mandatory context: DeviceUuid, Url.</summary>
    public const string ScpdFetch = "Scpd.Fetch";

    /// <summary>Mandatory context: DeviceUuid, Url; ErrorText for wrapped XmlException.</summary>
    public const string ScpdParse = "Scpd.Parse";

    // ─── SOAP (Story 3.1 — pre-added) ──────────────────────────────
    /// <summary>Mandatory context: DeviceUuid, Url, ActionName.</summary>
    public const string SoapInvoke = "Soap.Invoke";

    /// <summary>Mandatory context: DeviceUuid, Url, ActionName, StatusCode, ErrorText.</summary>
    public const string SoapFault = "Soap.Fault";

    // ─── GENA outbound (Story 4.2 — pre-added) ─────────────────────
    /// <summary>Mandatory context: DeviceUuid, Url, Sid (when known).</summary>
    public const string GenaSubscribe = "Gena.Subscribe";

    /// <summary>Mandatory context: DeviceUuid, Url; ErrorText.</summary>
    public const string GenaSubscribeFailed = "Gena.Subscribe.Failed";

    /// <summary>Mandatory context: DeviceUuid, Url, Sid.</summary>
    public const string GenaUnsubscribe = "Gena.Unsubscribe";

    /// <summary>Mandatory context: DeviceUuid, Url, Sid.</summary>
    public const string GenaUnsubscribeFailed = "Gena.Unsubscribe.Failed";

    /// <summary>Mandatory context: DeviceUuid, Url, Sid.</summary>
    public const string GenaRenewFailed = "Gena.Renew.Failed";

    // ─── GENA inbound callback host (Story 4.1 — pre-added) ────────
    /// <summary>Mandatory context: RemoteEndpoint; ErrorText.</summary>
    public const string GenaCallbackMalformed = "Gena.Callback.MalformedRequest";

    /// <summary>Mandatory context: RemoteEndpoint.</summary>
    public const string GenaCallbackOversize = "Gena.Callback.Oversize";

    /// <summary>Mandatory context: RemoteEndpoint.</summary>
    public const string GenaCallbackNoLength = "Gena.Callback.NoContentLength";

    /// <summary>Mandatory context: RemoteEndpoint.</summary>
    public const string GenaCallbackHeadersTo = "Gena.Callback.HeadersTimeout";

    /// <summary>Mandatory context: RemoteEndpoint.</summary>
    public const string GenaCallbackBodyTo = "Gena.Callback.BodyTimeout";

    /// <summary>Mandatory context: RemoteEndpoint.</summary>
    public const string GenaCallbackFlood = "Gena.Callback.ConnectionFlood";

    /// <summary>Mandatory context: Sid. Verbose severity by default.</summary>
    public const string GenaNotifyReceived = "Gena.Notify.Received";

    // ─── Adapter switch (Story 5.2 — pre-added) ────────────────────
    /// <summary>Mandatory context: (none beyond message).</summary>
    public const string AdapterSwitch = "Adapter.Switch";

    /// <summary>Mandatory context: (none beyond message).</summary>
    public const string AdapterSwitchTimeout = "Adapter.Switch.Timeout";

    // ─── Diagnostics infrastructure (Story 1.5 own use) ────────────
    /// <summary>Mandatory context: ErrorText. Emitted by DiagnosticFileSink on startup failure.</summary>
    public const string DiagnosticsFileSinkUnavailable = "Diagnostics.FileSink.Unavailable";

    // ─── XML viewing / shell-open (Story 2.8) ──────────────────────
    /// <summary>Mandatory context: Url; DeviceUuid when known. Emitted when a context-menu
    /// shell-open is refused (non-http(s) scheme) or fails (no default browser, etc.).</summary>
    public const string ShellExecute = "Shell.Execute";

    /// <summary>Mandatory context: (none beyond message). Temporary — emitted by the Story 2.8
    /// Subscribe stub (removed in Story 4.1) and the Properties stub (replaced in Story 2.9).
    /// A placeholder for menu items whose real handler lands in a later epic.</summary>
    public const string FeatureNotImplemented = "Feature.NotImplemented";
}
