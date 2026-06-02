namespace ohSpy.Core.Models;

using System.Net;
using System.Net.NetworkInformation;

/// <summary>
/// Pure-data projection of one OS network interface (FR-048 testability seam).
/// <see cref="System.Net.NetworkInformation.NetworkInterface"/> is sealed and not
/// constructible in tests; this record carries exactly the fields the eligibility
/// filter needs so <c>NetworkAdapterEnumerator</c> is unit-testable via a stubbed
/// <c>INetworkInterfaceSource</c>.
/// </summary>
/// <param name="Name">The OS friendly name of the interface.</param>
/// <param name="Description">The OS description of the interface.</param>
/// <param name="OperationalStatus">Whether the interface is up (eligibility gate).</param>
/// <param name="InterfaceType">The interface type (loopback is filtered out).</param>
/// <param name="SupportsMulticast">Whether the interface supports multicast (eligibility gate).</param>
/// <param name="UnicastAddresses">All unicast addresses bound to the interface (IPv4 + IPv6).</param>
public sealed record AdapterCandidate(
    string Name,
    string Description,
    OperationalStatus OperationalStatus,
    NetworkInterfaceType InterfaceType,
    bool SupportsMulticast,
    IReadOnlyList<IPAddress> UnicastAddresses);
