namespace ohSpy.Core.Devices;

using ohSpy.Core.Models;

/// <summary>
/// One UDN-keyed device in the registry (Decision 9 / Amendment A30). Carries SSDP metadata plus a
/// strict <see cref="DescriptionFetchState"/> machine: <c>Pending → InFlight →
/// Loaded/Failed</c>, with <c>Loaded</c>/<c>Failed</c> terminal. Mutating methods are
/// <c>internal</c> (only Core's dispatcher + tests drive the machine) and UI-thread-only
/// (callers marshal via <c>IUiDispatcher.Post</c>, Decision 1 + 9).
/// <para>
/// <see cref="DeviceCts"/> is the Decision 7 device level, linked to the adapter token so a
/// byebye / adapter switch cancels this device's in-flight fetch only (AC-7.2). Per AC-9.2
/// <see cref="Description"/> is non-null iff <see cref="State"/> is <see cref="DescriptionFetchState.Loaded"/>.
/// </para>
/// </summary>
public sealed class RegistryEntry
{
    /// <summary>The root device UDN (the opaque <c>uuid:&lt;body&gt;</c> string from the SSDP USN), the registry key.</summary>
    public string Udn { get; }

    /// <summary>The SSDP <c>LOCATION</c> URL the description is fetched from.</summary>
    public Uri LocationUrl { get; }

    /// <summary>Current fetch state (AC-9.1). Default <see cref="DescriptionFetchState.Pending"/>.</summary>
    public DescriptionFetchState State { get; private set; } = DescriptionFetchState.Pending;

    /// <summary>Parsed description; non-null iff <see cref="State"/> is Loaded (AC-9.2).</summary>
    public DeviceDescription? Description { get; private set; }

    /// <summary>Failure message; non-null iff <see cref="State"/> is Failed.</summary>
    public string? FailureReason { get; private set; }

    /// <summary>UTC of the first alive that created this entry.</summary>
    public DateTime FirstSeenUtc { get; }

    /// <summary>UTC of the most recent alive (updated by <see cref="RefreshSsdpMetadata"/>).</summary>
    public DateTime LastSeenUtc { get; private set; }

    /// <summary>Count of alive announcements observed for this entry instance.</summary>
    public int AliveCount { get; private set; }

    /// <summary>SSDP <c>SERVER</c> header from the latest alive.</summary>
    public string? Server { get; private set; }

    /// <summary>SSDP <c>CACHE-CONTROL: max-age</c> from the latest alive.</summary>
    public TimeSpan? CacheControlMaxAge { get; private set; }

    /// <summary><c>BOOTID.UPNP.ORG</c> (UDA 1.1) from the latest alive.</summary>
    public string? BootId { get; private set; }

    /// <summary><c>CONFIGID.UPNP.ORG</c> (UDA 1.1) from the latest alive.</summary>
    public string? ConfigId { get; private set; }

    /// <summary>Device-level cancellation source (Decision 7), linked to the adapter token.</summary>
    internal CancellationTokenSource DeviceCts { get; }

    /// <summary>
    /// Device-level cancellation token; safe to read from any thread. Stored as a field at
    /// construction time so it remains valid after the registry disposes <see cref="DeviceCts"/>
    /// on removal — callers checking <see cref="CancellationToken.IsCancellationRequested"/>
    /// after byebye will see <c>true</c> without triggering <see cref="ObjectDisposedException"/>.
    /// </summary>
    public CancellationToken DeviceToken { get; }

    /// <summary>
    /// Creates a Pending entry whose <see cref="DeviceCts"/> is linked to
    /// <paramref name="adapterToken"/> (AC-7.2). <see cref="AliveCount"/> starts at 0; the
    /// registry calls <see cref="RefreshSsdpMetadata"/> immediately to seed metadata and
    /// bump the count to 1.
    /// </summary>
    internal RegistryEntry(string udn, Uri locationUrl, DateTime nowUtc, CancellationToken adapterToken)
    {
        Udn = udn;
        LocationUrl = locationUrl;
        FirstSeenUtc = nowUtc;
        LastSeenUtc = nowUtc;
        DeviceCts = CancellationTokenSource.CreateLinkedTokenSource(adapterToken);
        DeviceToken = DeviceCts.Token; // snapshot before Dispose() could invalidate .Token
    }

    /// <summary>Pending → InFlight (AC-9.1). Throws if not Pending.</summary>
    internal void MarkInFlight()
    {
        Require(DescriptionFetchState.Pending);
        State = DescriptionFetchState.InFlight;
    }

    /// <summary>InFlight → Loaded (AC-9.1). Sets <see cref="Description"/>. Throws if not InFlight.</summary>
    internal void MarkLoaded(DeviceDescription description)
    {
        Require(DescriptionFetchState.InFlight);
        Description = description; // AC-9.2: set together with the terminal state.
        State = DescriptionFetchState.Loaded;
    }

    /// <summary>Pending/InFlight → Failed (AC-9.1). Sets <see cref="FailureReason"/>. Throws if terminal.</summary>
    internal void MarkFailed(string reason)
    {
        RequireAny(DescriptionFetchState.Pending, DescriptionFetchState.InFlight);
        FailureReason = reason;
        State = DescriptionFetchState.Failed;
    }

    /// <summary>
    /// Updates liveness + SSDP metadata on a subsequent alive (AC-9.4). Does NOT change
    /// <see cref="State"/> and triggers NO re-fetch (FR-043 cache invariant).
    /// </summary>
    internal void RefreshSsdpMetadata(DateTime nowUtc, string? server, TimeSpan? maxAge,
        string? bootId, string? configId)
    {
        LastSeenUtc = nowUtc;
        AliveCount++;
        Server = server;
        CacheControlMaxAge = maxAge;
        BootId = bootId;
        ConfigId = configId;
    }

    private void Require(DescriptionFetchState expected)
    {
        if (State != expected)
        {
            throw new InvalidOperationException(
                $"Illegal transition from {State}; expected {expected}.");
        }
    }

    private void RequireAny(DescriptionFetchState a, DescriptionFetchState b)
    {
        if (State != a && State != b)
        {
            throw new InvalidOperationException(
                $"Illegal transition from {State}; expected {a} or {b}.");
        }
    }
}
