namespace ohSpy.Core.Tests.Fakes;

using System.Net;
using System.Net.NetworkInformation;
using ohSpy.Core.Discovery;
using ohSpy.Core.Models;

/// <summary>
/// Stub <see cref="INetworkInterfaceSource"/> returning a caller-supplied candidate
/// list, so <c>NetworkAdapterEnumerator</c> eligibility filtering is unit-testable
/// without the unmockable BCL <c>NetworkInterface</c> (FR-048 test contract). The
/// <see cref="Eligible"/> helper builds a fully-eligible candidate that individual
/// tests then mutate via <c>with</c> to exercise one rejection reason at a time.
/// </summary>
internal sealed class StubNetworkInterfaceSource(IReadOnlyList<AdapterCandidate> candidates)
    : INetworkInterfaceSource
{
    public IReadOnlyList<AdapterCandidate> GetCandidates() => candidates;

    /// <summary>Builds a fully-eligible IPv4 candidate (up, non-loopback, multicast).</summary>
    public static AdapterCandidate Eligible(string name, string ipv4) => new(
        Name: name,
        Description: $"{name} (test)",
        OperationalStatus: OperationalStatus.Up,
        InterfaceType: NetworkInterfaceType.Ethernet,
        SupportsMulticast: true,
        UnicastAddresses: new[] { IPAddress.Parse(ipv4) });
}
