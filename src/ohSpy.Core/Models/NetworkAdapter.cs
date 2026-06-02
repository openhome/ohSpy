namespace ohSpy.Core.Models;

using System.Net;

/// <summary>
/// An eligible IPv4 network adapter (FR-048). Display-facing: the friendly
/// <see cref="Name"/> + <see cref="IPv4"/> populate the future View → Network
/// adapter radio list (Story 5.2). <see cref="IPv4"/> is the address the SSDP
/// transport binds to.
/// </summary>
/// <param name="Name">The OS friendly name of the interface.</param>
/// <param name="Description">The OS description (driver/hardware string) of the interface.</param>
/// <param name="IPv4">The selected IPv4 unicast address to bind the SSDP transport to.</param>
public sealed record NetworkAdapter(string Name, string Description, IPAddress IPv4);
