namespace ohSpy.Core.Scpd;

using ohSpy.Core.Models;

/// <summary>
/// Parses a UPnP device description XML document (the response to a GET of the SSDP
/// <c>LOCATION</c> URL). Synchronous because device descriptions are small (≤ 20 KB
/// typical; Decision 3 caps at 1 MB) — no need for incremental yield discipline.
/// <para>
/// The parser does NOT take ownership of the supplied byte array — caller may reuse it.
/// </para>
/// </summary>
public interface IDeviceDescriptionParser
{
    /// <summary>
    /// Parse <paramref name="xml"/>; return the root device's metadata plus a
    /// FLATTENED service list (root services + recursive embedded-device services
    /// per FR-053). Throws <see cref="Http.UpnpProtocolException"/> on malformed XML /
    /// XXE attempt / oversize document.
    /// </summary>
    DeviceDescription Parse(byte[] xml);
}
