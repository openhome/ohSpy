namespace ohSpy.Core.Tests.Discovery;

using FluentAssertions;
using ohSpy.Core.Discovery;
using Xunit;

public sealed class SsdpAnnouncementTests
{
    private static SsdpAnnouncement Make(string? nt) =>
        new(NT: nt, NTS: null, ST: null, USN: null, Udn: null,
            Location: null, CacheControlMaxAge: null, Server: null,
            BootId: null, ConfigId: null);

    [Fact]
    [Trait("ac", "AC-2.4.1")]
    public void IsRootDevice_WhenNtIsRootdevice_ReturnsTrue_AC241()
    {
        Make("upnp:rootdevice").IsRootDevice.Should().BeTrue();
    }

    [Fact]
    [Trait("ac", "AC-2.4.1")]
    public void IsRootDevice_WhenNtIsRootdevice_CaseInsensitive_AC241()
    {
        Make("UPNP:ROOTDEVICE").IsRootDevice.Should().BeTrue();
        Make("Upnp:RootDevice").IsRootDevice.Should().BeTrue();
    }

    [Fact]
    [Trait("ac", "AC-2.4.1")]
    public void IsRootDevice_WhenNtIsEmbedded_ReturnsFalse_AC241()
    {
        Make("urn:schemas-upnp-org:device:MediaRenderer:1").IsRootDevice.Should().BeFalse();
    }

    [Fact]
    [Trait("ac", "AC-2.4.1")]
    public void IsRootDevice_WhenNtIsNull_ReturnsFalse_AC241()
    {
        Make(null).IsRootDevice.Should().BeFalse();
    }
}
