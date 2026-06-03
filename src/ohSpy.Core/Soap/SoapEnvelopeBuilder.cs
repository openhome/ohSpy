namespace ohSpy.Core.Soap;

using System.Text;
using System.Xml;
using ohSpy.Core.Models;

/// <summary>
/// Builds a SOAP 1.1 action-invocation envelope (UDA 1.0 §3.2.1) from a structured
/// <see cref="SoapRequest"/>. This is the single point of XML framing + escaping for
/// outbound action calls — callers never hand-build envelope strings (that is where
/// escaping bugs ship).
/// </summary>
internal static class SoapEnvelopeBuilder
{
    private const string SoapEnvelopeNs = "http://schemas.xmlsoap.org/soap/envelope/";
    private const string SoapEncodingNs = "http://schemas.xmlsoap.org/soap/encoding/";

    /// <summary>
    /// Produces the SOAP envelope XML for <paramref name="req"/>. Output is UTF-8 in
    /// intent (the request later carries <c>Content-Type: text/xml; charset="utf-8"</c>);
    /// the XML declaration is deliberately omitted (see remarks).
    /// </summary>
    public static string Build(SoapRequest req)
    {
        ArgumentNullException.ThrowIfNull(req);

        var settings = new XmlWriterSettings
        {
            // OmitXmlDeclaration: an XmlWriter over a StringWriter (which is UTF-16) would
            // otherwise emit `<?xml version="1.0" encoding="utf-16"?>` — a wrong charset in
            // the declaration. UPnP devices key off the Content-Type header's charset anyway,
            // and the declaration is optional in the body per UDA 1.0 §3.2.1, so we omit it
            // entirely rather than emit a misleading one.
            OmitXmlDeclaration = true,
            Encoding = Encoding.UTF8,
        };

        var sb = new StringBuilder();
        using (var writer = XmlWriter.Create(sb, settings))
        {
            // <s:Envelope xmlns:s="..." s:encodingStyle="...">
            writer.WriteStartElement("s", "Envelope", SoapEnvelopeNs);
            writer.WriteAttributeString("s", "encodingStyle", SoapEnvelopeNs, SoapEncodingNs);

            // <s:Body>
            writer.WriteStartElement("s", "Body", SoapEnvelopeNs);

            // <u:ActionName xmlns:u="<serviceType>">
            // Writing the action element with the u prefix + serviceType namespace puts the
            // xmlns:u declaration exactly on the action element, as the spec shows.
            writer.WriteStartElement("u", req.ActionName, req.ServiceType);

            // One child per input argument, in order. WriteElementString auto-escapes the value
            // (< > & become entities); argument-less actions write no children, so XmlWriter
            // emits a self-closing <u:ActionName xmlns:u="..." /> (FR-031).
            foreach (var arg in req.InputArguments)
            {
                writer.WriteElementString(arg.Name, arg.Value);
            }

            writer.WriteEndElement(); // u:ActionName
            writer.WriteEndElement(); // s:Body
            writer.WriteEndElement(); // s:Envelope
        }

        return sb.ToString();
    }
}
