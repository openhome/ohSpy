namespace ohSpy.Core.Discovery;

using ohSpy.Core.Models;

/// <summary>
/// Enumerates eligible IPv4 adapters (FR-048): operational, non-loopback,
/// multicast-capable, with at least one IPv4 unicast address. Stable ordering —
/// the first entry is the launch default. Consumed by <c>AdapterScope</c> (startup
/// bind) and the View → Network adapter menu (Story 5.2).
/// </summary>
public interface INetworkAdapterEnumerator
{
    /// <summary>
    /// Returns the eligible adapters in deterministic source order. May be empty
    /// (zero-adapter host — the caller degrades gracefully per NFR-R5).
    /// </summary>
    IReadOnlyList<NetworkAdapter> Enumerate();
}
