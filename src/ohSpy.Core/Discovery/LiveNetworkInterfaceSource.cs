namespace ohSpy.Core.Discovery;

using System.Net.NetworkInformation;
using ohSpy.Core.Models;

/// <summary>
/// The live <see cref="INetworkInterfaceSource"/> — projects every OS network
/// interface into an <see cref="AdapterCandidate"/>. The ONLY type that calls the
/// BCL <see cref="NetworkInterface.GetAllNetworkInterfaces"/> (Pattern 7: registered
/// behind the interface; tests use a stub). Enumeration order is preserved so
/// "first eligible" stays deterministic (FR-048).
/// </summary>
internal sealed class LiveNetworkInterfaceSource : INetworkInterfaceSource
{
    public IReadOnlyList<AdapterCandidate> GetCandidates()
    {
        var result = new List<AdapterCandidate>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            // Project ALL unicast addresses (IPv4 + IPv6) faithfully; the enumerator
            // owns the IPv4 eligibility selection (single eligibility authority).
            var addresses = nic.GetIPProperties().UnicastAddresses
                .Select(u => u.Address)
                .ToArray();

            result.Add(new AdapterCandidate(
                nic.Name,
                nic.Description,
                nic.OperationalStatus,
                nic.NetworkInterfaceType,
                nic.SupportsMulticast,
                addresses));
        }

        return result;
    }
}
