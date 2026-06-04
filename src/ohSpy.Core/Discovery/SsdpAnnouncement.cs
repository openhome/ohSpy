namespace ohSpy.Core.Discovery;

/// <summary>
/// Parsed SSDP announcement. All header fields are nullable — a lenient parser omits
/// fields missing from the datagram. <see cref="IsRootDevice"/> is the FR-053 layer (b)
/// gate: only root-device announcements mutate the registry.
/// </summary>
public sealed record SsdpAnnouncement(
    string? NT,
    string? NTS,
    string? ST,
    string? USN,
    string? Udn,
    Uri? Location,
    TimeSpan? CacheControlMaxAge,
    string? Server,
    string? BootId,
    string? ConfigId)
{
    /// <summary>True iff NT == "upnp:rootdevice" (case-insensitive) — FR-053 layer (b).</summary>
    public bool IsRootDevice =>
        NT?.Equals("upnp:rootdevice", StringComparison.OrdinalIgnoreCase) == true;
}
