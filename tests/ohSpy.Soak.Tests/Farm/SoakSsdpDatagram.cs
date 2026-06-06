namespace ohSpy.Soak.Tests.Farm;

using System.Net;
using System.Text;
using ohSpy.Core.Models;

/// <summary>
/// Story 6.2 — builds raw SSDP datagrams for the farm advertiser (soak-scoped copy of the
/// <c>ohSpy.Core.Tests</c> <c>SsdpDatagramBuilder</c> wire format). Amendment A30: identity is the
/// opaque <c>uuid:&lt;udnBody&gt;</c> string; the resulting registry UDN is <c>uuid:{udnBody}</c>.
/// The LOCATION points at the per-device farm Kestrel endpoint so the real eager-description fetch
/// hits the farm's HTTP <c>/description.xml</c>.
/// </summary>
internal static class SoakSsdpDatagram
{
    private static readonly IPEndPoint FarmRemote = new(IPAddress.Loopback, 1900);

    public static SsdpDatagram Alive(string udnBody, string location) =>
        Notify("upnp:rootdevice", "ssdp:alive", udnBody, location);

    public static SsdpDatagram Byebye(string udnBody, string location) =>
        Notify("upnp:rootdevice", "ssdp:byebye", udnBody, location);

    public static SsdpDatagram Notify(string nt, string nts, string udnBody, string location) =>
        Build($"NOTIFY * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nNT: {nt}\r\nNTS: {nts}\r\n" +
              $"USN: uuid:{udnBody}::{nt}\r\nLOCATION: {location}\r\nCACHE-CONTROL: max-age=1800\r\n\r\n",
              SsdpSource.Multicast);

    private static SsdpDatagram Build(string text, SsdpSource source) =>
        new(FarmRemote, Encoding.UTF8.GetBytes(text), DateTime.UtcNow, source);
}
