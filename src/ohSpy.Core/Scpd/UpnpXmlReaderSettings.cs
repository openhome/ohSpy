namespace ohSpy.Core.Scpd;

using System.Xml;

/// <summary>
/// Single source of truth for <see cref="XmlReaderSettings"/> applied to ANY UPnP XML
/// parse (SCPD, device description, SOAP — anywhere we parse XML received from a LAN
/// device). XXE-locked: DTD prohibited, no external entity resolution, 4M character cap.
/// </summary>
internal static class UpnpXmlReaderSettings
{
    /// <summary>
    /// Returns a fresh <see cref="XmlReaderSettings"/> instance with the project's hardened
    /// settings. Each parse should construct its own (settings are mutable; sharing is
    /// fragile if any consumer mutates).
    /// </summary>
    public static XmlReaderSettings Create() => new XmlReaderSettings
    {
        Async = true,                              // required for ReadAsync (incremental SCPD parse)
        DtdProcessing = DtdProcessing.Prohibit,    // XXE defence — DOCTYPE/ENTITY raise XmlException
        XmlResolver = null,                        // defence-in-depth — no entity ever resolves to filesystem
        IgnoreComments = true,                     // simplify reader loop
        IgnoreWhitespace = true,                   // simplify reader loop
        MaxCharactersInDocument = 4_000_000,       // ~2 MB body cap from Decision 3, doubled for char-vs-byte
        // CloseInput is intentionally LEFT AT DEFAULT (false) — the parser does not own the
        // caller's stream lifetime. See IScpdParser / IDeviceDescriptionParser XML docs.
    };
}
