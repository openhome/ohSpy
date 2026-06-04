namespace ohSpy.Core.Tests.Diagnostics;

using FluentAssertions;
using ohSpy.Core.Devices;
using ohSpy.Core.Diagnostics;
using ohSpy.Core.Tests.Fakes;

/// <summary>
/// Story 2.3 — <see cref="RegistryIdentityLookup"/> (Decision 8 / FR-041). Resolves a
/// device UUID to its friendly name once the entry is Loaded; returns null otherwise so
/// the ring sink falls back to <c>uuid:&lt;uuid&gt;</c>.
/// </summary>
public sealed class RegistryIdentityLookupTests
{
    private static readonly Uri Location = new("http://192.0.2.10:49152/desc.xml");

    private static (DeviceRegistry Registry, RegistryIdentityLookup Lookup) NewPair()
    {
        var registry = new DeviceRegistry(new InlineUiDispatcher());
        return (registry, new RegistryIdentityLookup(registry));
    }

    private static void Alive(DeviceRegistry r, string udn) =>
        r.OnAlive(udn, Location, DateTime.UtcNow, "Linn/1.0", TimeSpan.FromSeconds(1800), "1", "1", default);

    [Fact]
    [Trait("ac", "AC-9.identity")]
    public void TryGetFriendlyName_LoadedEntry_ReturnsFriendlyName()
    {
        var (registry, lookup) = NewPair();
        var udn = $"uuid:{Guid.NewGuid()}";
        Alive(registry, udn);
        registry.TryGetEntry(udn, out var entry);
        entry.MarkInFlight();
        entry.MarkLoaded(StubDeviceDescriptionParser.Description(udn, "Bedroom DS"));

        lookup.TryGetFriendlyName(udn).Should().Be("Bedroom DS");
    }

    [Fact]
    [Trait("ac", "AC-9.identity")]
    public void TryGetFriendlyName_UnknownUdn_ReturnsNull()
    {
        var (_, lookup) = NewPair();

        lookup.TryGetFriendlyName($"uuid:{Guid.NewGuid()}").Should().BeNull();
    }

    [Fact]
    [Trait("ac", "AC-9.identity")]
    public void TryGetFriendlyName_PendingEntry_ReturnsNull()
    {
        var (registry, lookup) = NewPair();
        var udn = $"uuid:{Guid.NewGuid()}";
        Alive(registry, udn); // Pending — no Description yet

        lookup.TryGetFriendlyName(udn).Should().BeNull(
            "a not-yet-Loaded device has no friendly name; ring sink falls back to the UDN string");
    }
}
