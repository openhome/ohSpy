namespace ohSpy.Core.Tests.Fakes;

using ohSpy.Core.Models;
using ohSpy.Core.Scpd;

/// <summary>
/// Test double for <see cref="IDeviceDescriptionParser"/> that returns a caller-supplied
/// <see cref="DeviceDescription"/> (or throws via the responder), so dispatcher tests can
/// control the parsed UDN without crafting XML. Use the real parser for the
/// malformed-bytes path.
/// </summary>
internal sealed class StubDeviceDescriptionParser : IDeviceDescriptionParser
{
    public Func<byte[], DeviceDescription> Responder { get; set; } =
        _ => throw new InvalidOperationException("StubDeviceDescriptionParser.Responder not set");

    public DeviceDescription Parse(byte[] xml) => Responder(xml);

    /// <summary>Builds a minimal <see cref="DeviceDescription"/> with the given UDN + friendly name.</summary>
    public static DeviceDescription Description(string udn, string friendlyName = "Test Device") => new(
        FriendlyName: friendlyName,
        DeviceType: "urn:schemas-upnp-org:device:MediaRenderer:1",
        Udn: udn,
        PresentationUrl: null,
        Manufacturer: "Linn",
        ManufacturerUrl: null,
        ModelName: "TestModel",
        ModelNumber: null,
        ModelDescription: null,
        ModelUrl: null,
        SerialNumber: null,
        Upc: null,
        Services: Array.Empty<ServiceDescription>());
}
