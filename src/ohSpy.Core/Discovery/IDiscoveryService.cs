namespace ohSpy.Core.Discovery;

/// <summary>Reads <see cref="ISsdpTransport.IncomingDatagrams"/> and routes announcements into the registry.</summary>
public interface IDiscoveryService : IAsyncDisposable
{
    /// <summary>Raised on the UI thread for every successfully parsed announcement (FR-014/FR-015).</summary>
    event Action<SsdpAnnouncement> AnnouncementReceived;

    /// <summary>Starts consuming <see cref="ISsdpTransport.IncomingDatagrams"/>.</summary>
    Task StartAsync(CancellationToken adapterToken, CancellationToken ct);
}
