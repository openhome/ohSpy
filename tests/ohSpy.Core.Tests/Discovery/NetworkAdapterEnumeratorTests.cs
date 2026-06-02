namespace ohSpy.Core.Tests.Discovery;

using System.Net;
using System.Net.NetworkInformation;
using FluentAssertions;
using ohSpy.Core.Discovery;
using ohSpy.Core.Models;
using ohSpy.Core.Tests.Fakes;

/// <summary>
/// Story 2.2 — <c>NetworkAdapterEnumerator</c> eligibility filtering (FR-048), driven
/// through a stubbed <see cref="INetworkInterfaceSource"/> so no unmockable BCL
/// <c>NetworkInterface</c> is involved. Every AC-traceable test carries
/// <c>[Trait("ac", "AC-2.2.&lt;n&gt;")]</c> (Amendment A2). The dev-machine test
/// carries <c>[Trait("category", "integration")]</c> (Pattern 14) so the chaos-hook
/// filter <c>category=chaos</c> does not sweep it (A18).
/// </summary>
public sealed class NetworkAdapterEnumeratorTests
{
    private static NetworkAdapterEnumerator Build(params AdapterCandidate[] candidates) =>
        new(new StubNetworkInterfaceSource(candidates));

    [Fact]
    [Trait("ac", "AC-2.2.1")]
    public void Enumerate_NoCandidates_ReturnsEmpty_AC221()
    {
        Build().Enumerate().Should().BeEmpty();
    }

    [Fact]
    [Trait("ac", "AC-2.2.1")]
    public void Enumerate_OneEligible_ReturnsItWithDisplayFields_AC221()
    {
        var candidate = StubNetworkInterfaceSource.Eligible("Ethernet0", "192.168.1.50");

        var result = Build(candidate);

        result.Enumerate().Should().ContainSingle().Which.Should().BeEquivalentTo(
            new NetworkAdapter("Ethernet0", "Ethernet0 (test)", IPAddress.Parse("192.168.1.50")));
    }

    [Fact]
    [Trait("ac", "AC-2.2.1")]
    public void Enumerate_DownAdapter_Filtered_AC221()
    {
        var down = StubNetworkInterfaceSource.Eligible("Ethernet0", "192.168.1.50")
            with { OperationalStatus = OperationalStatus.Down };

        Build(down).Enumerate().Should().BeEmpty();
    }

    [Fact]
    [Trait("ac", "AC-2.2.1")]
    public void Enumerate_Loopback_Filtered_AC221()
    {
        var loopback = StubNetworkInterfaceSource.Eligible("Loopback", "127.0.0.1")
            with { InterfaceType = NetworkInterfaceType.Loopback };

        Build(loopback).Enumerate().Should().BeEmpty();
    }

    [Fact]
    [Trait("ac", "AC-2.2.1")]
    public void Enumerate_NonMulticast_Filtered_AC221()
    {
        var noMulticast = StubNetworkInterfaceSource.Eligible("Ethernet0", "192.168.1.50")
            with { SupportsMulticast = false };

        Build(noMulticast).Enumerate().Should().BeEmpty();
    }

    [Fact]
    [Trait("ac", "AC-2.2.1")]
    public void Enumerate_IPv6Only_Filtered_AC221()
    {
        var ipv6Only = StubNetworkInterfaceSource.Eligible("Ethernet0", "192.168.1.50")
            with { UnicastAddresses = new[] { IPAddress.Parse("fe80::1") } };

        Build(ipv6Only).Enumerate().Should().BeEmpty();
    }

    [Fact]
    [Trait("ac", "AC-2.2.1")]
    public void Enumerate_PreservesSourceOrder_AC221()
    {
        var a = StubNetworkInterfaceSource.Eligible("A", "192.168.1.10");
        var b = StubNetworkInterfaceSource.Eligible("B", "192.168.1.11");
        var c = StubNetworkInterfaceSource.Eligible("C", "192.168.1.12");

        Build(a, b, c).Enumerate().Select(n => n.Name)
            .Should().ContainInOrder("A", "B", "C");
    }

    [Fact]
    [Trait("ac", "AC-2.2.1")]
    public void Enumerate_SkipsIneligibleButKeepsOrderOfEligible_AC221()
    {
        var a = StubNetworkInterfaceSource.Eligible("A", "192.168.1.10");
        var downB = StubNetworkInterfaceSource.Eligible("B", "192.168.1.11")
            with { OperationalStatus = OperationalStatus.Down };
        var c = StubNetworkInterfaceSource.Eligible("C", "192.168.1.12");

        Build(a, downB, c).Enumerate().Select(n => n.Name)
            .Should().ContainInOrder("A", "C");
    }

    [Fact]
    [Trait("ac", "AC-2.2.1")]
    public void Enumerate_MultipleIPv4_PicksFirst_AC221()
    {
        var multi = StubNetworkInterfaceSource.Eligible("Ethernet0", "192.168.1.50")
            with
            {
                UnicastAddresses = new[]
                {
                    IPAddress.Parse("10.0.0.5"),
                    IPAddress.Parse("192.168.1.50"),
                },
            };

        Build(multi).Enumerate().Should().ContainSingle()
            .Which.IPv4.Should().Be(IPAddress.Parse("10.0.0.5"));
    }

    [Fact]
    [Trait("ac", "AC-2.2.1")]
    public void Enumerate_MixedV6ThenV4_PicksFirstV4_AC221()
    {
        var mixed = StubNetworkInterfaceSource.Eligible("Ethernet0", "192.168.1.50")
            with
            {
                UnicastAddresses = new[]
                {
                    IPAddress.Parse("fe80::1"),
                    IPAddress.Parse("192.168.1.50"),
                },
            };

        Build(mixed).Enumerate().Should().ContainSingle()
            .Which.IPv4.Should().Be(IPAddress.Parse("192.168.1.50"));
    }

    // ─── Integration: live BCL source on the dev machine ───────────────────────

    [Fact]
    [Trait("ac", "AC-2.2.9")]
    [Trait("category", "integration")]
    public void Enumerate_DevMachine_HasAtLeastOneEligible_AC229()
    {
        var enumerator = new NetworkAdapterEnumerator(new LiveNetworkInterfaceSource());

        var adapters = enumerator.Enumerate();

        // Epic-1 retro action B: 0 here on a networked box is a red flag, not a pass.
        adapters.Should().NotBeEmpty(
            "a dev machine has at least one up, non-loopback, multicast IPv4 adapter");
        adapters.Should().OnlyContain(a => a.IPv4.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
    }
}
