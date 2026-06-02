namespace ohSpy.Core.Diagnostics;

using ohSpy.Core.Devices;

/// <summary>
/// Registry-backed <see cref="IDiagnosticIdentityLookup"/> (Story 2.3) — replaces
/// <see cref="NullIdentityLookup"/>. Resolves a device UUID to its friendly name for the
/// FR-041 Identity column.
/// <para>
/// Thread-safety: <see cref="DiagnosticRingSink"/> resolves identity on the emitting thread
/// (which may be a background fetch task), so <see cref="TryGetFriendlyName"/> runs off the
/// UI thread. The registry's <c>ConcurrentDictionary</c> makes <c>TryGetEntry</c> safe, and
/// the <see cref="RegistryEntry.Description"/> reference read is atomic — a slightly-stale
/// null just yields the <c>uuid:&lt;uuid&gt;</c> fallback the contract permits.
/// </para>
/// </summary>
internal sealed class RegistryIdentityLookup(IDeviceRegistry registry) : IDiagnosticIdentityLookup
{
    public string? TryGetFriendlyName(Guid deviceUuid) =>
        registry.TryGetEntry(deviceUuid, out var entry) ? entry.Description?.FriendlyName : null;
}
