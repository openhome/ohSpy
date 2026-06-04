namespace ohSpy.Core.Discovery;

using System.Threading.Channels;
using ohSpy.Core.Models;

/// <summary>Reads a transport's <c>IncomingDatagrams</c> reader and routes announcements into the registry.</summary>
public interface IDiscoveryService : IAsyncDisposable
{
    /// <summary>Raised on the UI thread for every successfully parsed announcement (FR-014/FR-015).</summary>
    event Action<SsdpAnnouncement> AnnouncementReceived;

    /// <summary>
    /// Starts consuming <paramref name="reader"/> (the scope-owned transport's datagram reader, A23).
    /// </summary>
    Task StartAsync(ChannelReader<SsdpDatagram> reader, CancellationToken adapterToken, CancellationToken ct);

    /// <summary>
    /// Story 5.2 atomic rebind: drains the current read loop and starts a fresh one against the new
    /// adapter scope's <paramref name="reader"/> + token, WITHOUT re-subscribing the app-lifetime
    /// <see cref="AnnouncementReceived"/> consumers (the singleton service persists; only its loop rebinds).
    /// </summary>
    Task RebindAsync(ChannelReader<SsdpDatagram> reader, CancellationToken adapterToken, CancellationToken ct);
}
