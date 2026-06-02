namespace ohSpy.Core.Diagnostics;

/// <summary>
/// Forward-dependency bridge for <see cref="DiagnosticRingSink"/>'s FR-041 Identity column
/// resolution. Story 1.5 ships <see cref="NullIdentityLookup"/> (always returns null);
/// Story 2.3 introduces <c>IDeviceRegistry</c> and replaces the DI registration with a
/// registry-backed implementation. <see cref="DiagnosticRingSink"/> is unchanged across
/// the swap — the contract is stable.
/// </summary>
public interface IDiagnosticIdentityLookup
{
    /// <summary>
    /// Return the friendly name registered for <paramref name="deviceUuid"/>, or null if the
    /// device isn't in the registry OR has no friendly name yet.
    /// </summary>
    string? TryGetFriendlyName(Guid deviceUuid);
}
