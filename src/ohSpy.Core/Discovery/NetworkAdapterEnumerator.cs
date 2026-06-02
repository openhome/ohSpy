namespace ohSpy.Core.Discovery;

using System.Net.NetworkInformation;
using System.Net.Sockets;
using ohSpy.Core.Models;

/// <summary>
/// Filters the <see cref="INetworkInterfaceSource"/> projection down to eligible
/// IPv4 adapters (FR-048). Each <c>continue</c> guard maps to one eligibility rule
/// and is exercised by a dedicated unit test. Pure query — no diagnostics; the
/// zero-adapter Warning is the <c>AdapterScope</c>'s concern.
/// </summary>
internal sealed class NetworkAdapterEnumerator(INetworkInterfaceSource source)
    : INetworkAdapterEnumerator
{
    public IReadOnlyList<NetworkAdapter> Enumerate()
    {
        var result = new List<NetworkAdapter>();
        foreach (var c in source.GetCandidates())
        {
            if (c.OperationalStatus != OperationalStatus.Up) continue;   // must be up
            if (c.InterfaceType == NetworkInterfaceType.Loopback) continue; // non-loopback
            if (!c.SupportsMulticast) continue;                          // multicast-capable

            var ipv4 = c.UnicastAddresses
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            if (ipv4 is null) continue;                                  // must have IPv4

            result.Add(new NetworkAdapter(c.Name, c.Description, ipv4));
        }

        return result;
    }
}
