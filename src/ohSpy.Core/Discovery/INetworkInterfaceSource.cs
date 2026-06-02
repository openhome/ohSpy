namespace ohSpy.Core.Discovery;

using ohSpy.Core.Models;

/// <summary>
/// Abstraction over <c>NetworkInterface.GetAllNetworkInterfaces()</c> so adapter
/// eligibility filtering is unit-testable (FR-048 test contract). The live impl
/// (<see cref="LiveNetworkInterfaceSource"/>) is the ONLY type that touches the BCL
/// NIC API; tests inject a stub returning synthetic <see cref="AdapterCandidate"/>s.
/// </summary>
public interface INetworkInterfaceSource
{
    /// <summary>
    /// Returns one <see cref="AdapterCandidate"/> per OS network interface, in the
    /// underlying enumeration order. Eligibility filtering is the enumerator's job,
    /// not the source's — the source is a faithful projection.
    /// </summary>
    IReadOnlyList<AdapterCandidate> GetCandidates();
}
