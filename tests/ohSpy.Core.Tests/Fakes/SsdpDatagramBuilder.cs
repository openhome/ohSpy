namespace ohSpy.Core.Tests.Fakes;

using System.Net;
using System.Text;
using ohSpy.Core.Models;

internal static class SsdpDatagramBuilder
{
    private static readonly IPEndPoint TestRemote =
        new(IPAddress.Parse("192.0.2.42"), 50000);

    // Amendment A30: identity is the opaque UDN string. `udnBody` is the part AFTER `uuid:`
    // (a GUID string for RFC-4122 devices, or any opaque token for non-RFC-4122 devices like Linn).
    // The resulting registry UDN is "uuid:{udnBody}".
    public static SsdpDatagram Notify(string nt, string nts, string udnBody,
        string location = "http://192.0.2.42:49152/desc.xml") =>
        Build($"NOTIFY * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nNT: {nt}\r\nNTS: {nts}\r\n" +
              $"USN: uuid:{udnBody}::{nt}\r\nLOCATION: {location}\r\nCACHE-CONTROL: max-age=1800\r\n\r\n",
              SsdpSource.Multicast);

    public static SsdpDatagram SearchResponse(string udnBody,
        string location = "http://192.0.2.42:49152/desc.xml") =>
        Build($"HTTP/1.1 200 OK\r\nST: upnp:rootdevice\r\n" +
              $"USN: uuid:{udnBody}::upnp:rootdevice\r\nLOCATION: {location}\r\n" +
              $"CACHE-CONTROL: max-age=1800\r\n\r\n",
              SsdpSource.SearchResponse);

    public static SsdpDatagram Malformed() =>
        Build("NOT_SSDP garbage\r\n", SsdpSource.Multicast);

    private static SsdpDatagram Build(string text, SsdpSource source) =>
        new(TestRemote, Encoding.UTF8.GetBytes(text), DateTime.UtcNow, source);
}
