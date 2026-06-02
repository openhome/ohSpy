namespace ohSpy.Core.Scpd;

using System.Xml;
using ohSpy.Core.Http;
using ohSpy.Core.Models;

/// <summary>
/// <see cref="XmlReader"/>-backed implementation of <see cref="IDeviceDescriptionParser"/>.
/// Synchronous: <see cref="XmlReader.Read"/> over a <see cref="MemoryStream"/>. Same
/// XXE-locked settings as <see cref="XmlReaderScpdParser"/>.
/// </summary>
internal sealed class DeviceDescriptionParser : IDeviceDescriptionParser
{
    private static readonly Uri PlaceholderUri = new Uri("about:blank");

    public DeviceDescription Parse(byte[] xml)
    {
        ArgumentNullException.ThrowIfNull(xml);
        using var stream = new MemoryStream(xml);
        // Sync API — XmlReaderSettings.Async still true (it's a property of capability,
        // not of usage; synchronous reads work on an async-capable reader).
        using var reader = XmlReader.Create(stream, UpnpXmlReaderSettings.Create());

        try
        {
            // Navigate to the root <device> element. Skip <?xml?>, <root>, <specVersion>.
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "device")
                    return ReadDevice(reader, includeEmbeddedServices: true);
            }
            throw new UpnpProtocolException(PlaceholderUri, "device description missing root <device>");
        }
        catch (XmlException ex)
        {
            throw new UpnpProtocolException(PlaceholderUri, $"device description XML parse failed: {ex.Message}");
        }
    }

    // Reads ONE <device> element. If includeEmbeddedServices=true, recurses into
    // <deviceList><device> entries and appends their services (FR-053 flattening).
    //
    // CONTROL-FLOW NOTE: XmlReader.ReadElementContentAsString returns the reader positioned
    // PAST the matching </element>, i.e., at the NEXT node. Calling reader.Read() again at
    // the top of the outer loop would then skip that next node. We therefore use a manual
    // "advance only when we didn't already advance" pattern via the `advanced` flag.
    private static DeviceDescription ReadDevice(XmlReader reader, bool includeEmbeddedServices)
    {
        string friendlyName = "", deviceType = "", udn = "", manufacturer = "", modelName = "";
        string? presentationUrl = null, manufacturerUrl = null, modelNumber = null,
                modelDescription = null, modelUrl = null, serialNumber = null, upc = null;
        var services = new List<ServiceDescription>();

        if (reader.IsEmptyElement)
        {
            return new DeviceDescription(
                friendlyName, deviceType, udn,
                presentationUrl, manufacturer, manufacturerUrl,
                modelName, modelNumber, modelDescription, modelUrl,
                serialNumber, upc, services);
        }

        var depth = reader.Depth;
        var advanced = false;
        while (advanced || reader.Read())
        {
            advanced = false;
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
                break;
            if (reader.NodeType != XmlNodeType.Element)
                continue;

            switch (reader.LocalName)
            {
                case "friendlyName":     friendlyName     = reader.ReadElementContentAsString(); advanced = true; break;
                case "deviceType":       deviceType       = reader.ReadElementContentAsString(); advanced = true; break;
                case "UDN":              udn              = reader.ReadElementContentAsString(); advanced = true; break;
                case "presentationURL":  presentationUrl  = reader.ReadElementContentAsString(); advanced = true; break;
                case "manufacturer":     manufacturer     = reader.ReadElementContentAsString(); advanced = true; break;
                case "manufacturerURL":  manufacturerUrl  = reader.ReadElementContentAsString(); advanced = true; break;
                case "modelName":        modelName        = reader.ReadElementContentAsString(); advanced = true; break;
                case "modelNumber":      modelNumber      = reader.ReadElementContentAsString(); advanced = true; break;
                case "modelDescription": modelDescription = reader.ReadElementContentAsString(); advanced = true; break;
                case "modelURL":         modelUrl         = reader.ReadElementContentAsString(); advanced = true; break;
                case "serialNumber":     serialNumber     = reader.ReadElementContentAsString(); advanced = true; break;
                case "UPC":              upc              = reader.ReadElementContentAsString(); advanced = true; break;
                case "serviceList":      ReadServiceList(reader, services); break;
                case "deviceList":       if (includeEmbeddedServices) ReadEmbeddedDeviceList(reader, services); break;
            }
        }
        return new DeviceDescription(
            friendlyName, deviceType, udn,
            presentationUrl, manufacturer, manufacturerUrl,
            modelName, modelNumber, modelDescription, modelUrl,
            serialNumber, upc, services);
    }

    private static void ReadServiceList(XmlReader reader, List<ServiceDescription> sink)
    {
        if (reader.IsEmptyElement) return;

        var depth = reader.Depth;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth) break;
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "service")
                sink.Add(ReadService(reader));
        }
    }

    private static ServiceDescription ReadService(XmlReader reader)
    {
        string serviceType = "", serviceId = "", scpdUrl = "", controlUrl = "", eventSubUrl = "";

        if (reader.IsEmptyElement)
            return new ServiceDescription(serviceType, serviceId, scpdUrl, controlUrl, eventSubUrl);

        var depth = reader.Depth;
        var advanced = false;
        while (advanced || reader.Read())
        {
            advanced = false;
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth) break;
            if (reader.NodeType != XmlNodeType.Element) continue;
            switch (reader.LocalName)
            {
                case "serviceType": serviceType = reader.ReadElementContentAsString(); advanced = true; break;
                case "serviceId":   serviceId   = reader.ReadElementContentAsString(); advanced = true; break;
                case "SCPDURL":     scpdUrl     = reader.ReadElementContentAsString(); advanced = true; break;
                case "controlURL":  controlUrl  = reader.ReadElementContentAsString(); advanced = true; break;
                case "eventSubURL": eventSubUrl = reader.ReadElementContentAsString(); advanced = true; break;
            }
        }
        return new ServiceDescription(serviceType, serviceId, scpdUrl, controlUrl, eventSubUrl);
    }

    // FR-053 flattening: walk <deviceList><device>+ recursively, append every embedded
    // device's services to the SAME root services list. Embedded device metadata is
    // discarded (not tracked as a separate device; only roots register).
    private static void ReadEmbeddedDeviceList(XmlReader reader, List<ServiceDescription> rootSink)
    {
        if (reader.IsEmptyElement) return;

        var depth = reader.Depth;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth) break;
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "device")
            {
                // Recurse: read this embedded device, but FLATTEN — capture its services
                // (and its embedded children's services) into the same rootSink.
                var embedded = ReadDevice(reader, includeEmbeddedServices: true);
                rootSink.AddRange(embedded.Services);
            }
        }
    }
}
