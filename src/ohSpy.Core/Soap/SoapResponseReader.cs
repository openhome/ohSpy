namespace ohSpy.Core.Soap;

using System.Xml;
using ohSpy.Core.Models;
using ohSpy.Core.Scpd;

/// <summary>
/// Lifts output arguments out of a SOAP action-response envelope. Navigates into
/// <c>s:Body</c>, finds the single <c>&lt;u:ActionNameResponse&gt;</c> element, and
/// collects each direct child element as a <see cref="SoapArgument"/> (element local-name
/// + text content). XmlReader unescapes entities on read, so values come back as plain
/// text. An argument-less response yields an empty list. Reuses the shared XXE-locked
/// reader settings (defence-in-depth on the success path too).
/// </summary>
internal static class SoapResponseReader
{
    public static IReadOnlyList<SoapArgument> ReadOutputArguments(byte[] body)
    {
        ArgumentNullException.ThrowIfNull(body);

        var args = new List<SoapArgument>();
        using var stream = new MemoryStream(body, writable: false);
        using var reader = XmlReader.Create(stream, UpnpXmlReaderSettings.Create());

        reader.MoveToContent();

        // Descend to the Body element.
        if (!AdvanceToElement(reader, "Body"))
        {
            return args;
        }

        // The first child element of Body is the action-response wrapper (<u:*Response>).
        if (!AdvanceToAnyChildElement(reader))
        {
            return args; // empty Body, or no response wrapper.
        }

        // If the response wrapper is self-closing / empty, there are no output args.
        if (reader.IsEmptyElement)
        {
            return args;
        }

        var wrapperDepth = reader.Depth;

        // Walk the direct children of the response wrapper; each is one output argument.
        // Drive the reader manually: ReadElementContentAsString() consumes the element AND its
        // EndElement and lands on the NEXT sibling, so we must NOT also call Read() in that case
        // (that would skip the sibling). Only advance via Read() when we didn't consume an arg.
        reader.Read(); // step into the wrapper's first child node.
        while (!reader.EOF)
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == wrapperDepth)
            {
                break; // end of the response wrapper.
            }

            if (reader.NodeType == XmlNodeType.Element && reader.Depth == wrapperDepth + 1)
            {
                var name = reader.LocalName;
                var value = reader.ReadElementContentAsString(); // unescapes + advances to next sibling.
                args.Add(new SoapArgument(name, value));
                continue; // already positioned on the next node — don't double-advance.
            }

            reader.Read();
        }

        return args;
    }

    // Advances the reader until the current node is an Element with the given local name.
    private static bool AdvanceToElement(XmlReader reader, string localName)
    {
        while (!reader.EOF)
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == localName)
            {
                return true;
            }
            reader.Read();
        }
        return false;
    }

    // From the current element, advances to its first child element (if any).
    private static bool AdvanceToAnyChildElement(XmlReader reader)
    {
        if (reader.IsEmptyElement)
        {
            return false;
        }
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                return true;
            }
            if (reader.NodeType == XmlNodeType.EndElement)
            {
                return false; // reached end of parent with no child element.
            }
        }
        return false;
    }
}
