namespace ohSpy.Core.Models;

/// <summary>
/// Identifies which of the two per-adapter UDP sockets received an
/// <see cref="SsdpDatagram"/> (Decision 2): the multicast listener bound to
/// <c>(adapter, 1900)</c> or the ephemeral search socket that issues M-SEARCH.
/// </summary>
public enum SsdpSource
{
    /// <summary>Datagram arrived on the multicast listener (NOTIFY advertisements).</summary>
    Multicast,

    /// <summary>Datagram arrived on the ephemeral search socket (M-SEARCH responses).</summary>
    SearchResponse,
}
