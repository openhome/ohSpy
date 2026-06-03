namespace ohSpy.Core.Soap;

using System.Xml;
using ohSpy.Core.Scpd;

/// <summary>
/// Parses a SOAP 500 fault body into a structured <see cref="UpnpFault"/>. Returns
/// <c>false</c> (a generic transport error for the caller) for any body that is not a
/// well-formed UPnP fault — a raw fault string, a missing/zero <c>&lt;errorCode&gt;</c>,
/// malformed XML, or an XXE attempt. Reuses the shared XXE-locked reader settings.
/// </summary>
internal static class SoapFaultParser
{
    /// <summary>
    /// Attempts to extract <c>errorCode</c> + <c>errorDescription</c> from a SOAP fault body.
    /// </summary>
    /// <param name="body">Raw response bytes (UTF-8 SOAP fault envelope).</param>
    /// <param name="fault">On <c>true</c>, the parsed fault; on <c>false</c>, an unspecified sentinel.</param>
    /// <returns><c>true</c> iff a non-zero <c>errorCode</c> was parsed.</returns>
    public static bool TryParse(byte[] body, out UpnpFault fault)
    {
        ArgumentNullException.ThrowIfNull(body);

        int errorCode = 0;
        string errorDescription = string.Empty;
        try
        {
            // Shared XXE-locked settings (DtdProcessing.Prohibit, XmlResolver=null, 4M char cap).
            // A DOCTYPE / external-entity (XXE) attempt raises XmlException → caught → false.
            using var stream = new MemoryStream(body, writable: false);
            using var reader = XmlReader.Create(stream, UpnpXmlReaderSettings.Create());

            // NOTE: don't combine `while(reader.Read())` with `ReadElementContentAsString()` —
            // the latter advances past EndElement on its own, so a Read() in the loop header
            // would skip the next node. Drive the reader manually.
            reader.MoveToContent();
            while (!reader.EOF)
            {
                if (reader.NodeType == XmlNodeType.Element)
                {
                    if (reader.LocalName == "errorCode")
                    {
                        var raw = reader.ReadElementContentAsString();
                        // Deliberate: errorCode stays 0 on parse failure; the `errorCode != 0`
                        // gate below then correctly treats it as "not a UPnPError".
                        _ = int.TryParse(raw, out errorCode);
                        continue;
                    }
                    if (reader.LocalName == "errorDescription")
                    {
                        errorDescription = reader.ReadElementContentAsString();
                        continue;
                    }
                }
                reader.Read();
            }
        }
        catch (XmlException)
        {
            fault = default!;
            return false;
        }

        if (errorCode == 0)
        {
            fault = default!;
            return false;
        }

        fault = new UpnpFault(errorCode, errorDescription);
        return true;
    }
}
