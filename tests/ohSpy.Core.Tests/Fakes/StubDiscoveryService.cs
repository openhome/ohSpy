namespace ohSpy.Core.Tests.Fakes;

using System.Threading.Channels;
using ohSpy.Core.Discovery;
using ohSpy.Core.Models;

/// <summary>
/// Controllable <see cref="IDiscoveryService"/> double for log-VM tests. The real service
/// raises <see cref="AnnouncementReceived"/> on the UI thread; <see cref="Raise"/> invokes it
/// synchronously so a test paired with <see cref="InlineUiDispatcher"/> is deterministic.
/// </summary>
internal sealed class StubDiscoveryService : IDiscoveryService
{
    public event Action<SsdpAnnouncement>? AnnouncementReceived;

    /// <summary>Raise the event as the real service would (already on the UI thread).</summary>
    public void Raise(SsdpAnnouncement ann) => AnnouncementReceived?.Invoke(ann);

    public Task StartAsync(ChannelReader<SsdpDatagram> reader, CancellationToken adapterToken, CancellationToken ct) =>
        Task.CompletedTask;

    public Task RebindAsync(ChannelReader<SsdpDatagram> reader, CancellationToken adapterToken, CancellationToken ct) =>
        Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
