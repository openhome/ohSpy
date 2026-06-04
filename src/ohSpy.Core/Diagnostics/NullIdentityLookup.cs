namespace ohSpy.Core.Diagnostics;

/// <summary>
/// Placeholder <see cref="IDiagnosticIdentityLookup"/> for use before Story 2.3 ships the
/// device registry. Always returns null — every <see cref="DiagnosticRow.IdentityLabel"/>
/// falls back to the UDN string until Story 2.3 swaps in the real lookup.
/// </summary>
internal sealed class NullIdentityLookup : IDiagnosticIdentityLookup
{
    public string? TryGetFriendlyName(string udn) => null;
}
