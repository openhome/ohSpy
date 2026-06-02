namespace ohSpy.Core.Models;

using System.Net;

/// <summary>
/// A single raw datagram received by <c>SsdpTransport</c> (Decision 2). Pure data
/// carrier (Pattern 9) — no parsing happens here; <see cref="Payload"/> is the raw
/// wire bytes. The <c>SsdpParser</c> (Story 2.4) turns these into announcements.
/// </summary>
/// <param name="Remote">The sender's endpoint (address + port) as reported by the socket.</param>
/// <param name="Payload">The raw datagram bytes, exactly <c>ReceivedBytes</c> long (no slack).</param>
/// <param name="ArrivalUtc">UTC timestamp stamped by the receive loop on wakeup (Pattern 9).</param>
/// <param name="Source">Which socket received it (Decision 2 source-tagging).</param>
public sealed record SsdpDatagram(
    IPEndPoint Remote,
    byte[] Payload,
    DateTime ArrivalUtc,
    SsdpSource Source);
