namespace ohSpy.Core.Discovery;

using System.Net;
using System.Threading.Channels;
using ohSpy.Core.Models;

/// <summary>
/// Per-adapter UDP transport for SSDP datagrams. One instance per active
/// adapter; <see cref="System.IAsyncDisposable.DisposeAsync"/> is part of the
/// FR-050 atomic adapter-switch sequence (Decision 7).
/// </summary>
public interface ISsdpTransport : IAsyncDisposable
{
    /// <summary>
    /// Binds the multicast listener on <c>(adapterIPv4, 1900)</c> and an ephemeral
    /// search socket on <c>(adapterIPv4, 0)</c>, joins the SSDP multicast group, and
    /// starts both receive loops (Decision 2).
    /// </summary>
    /// <param name="adapterIPv4">The adapter address to bind both sockets to (FR-048 single-adapter).</param>
    /// <param name="ct">
    /// The adapter-level cancellation token (Decision 7). When cancelled, both receive
    /// loops exit cleanly; teardown still flows through <see cref="System.IAsyncDisposable.DisposeAsync"/>.
    /// </param>
    /// <remarks>Pre: not already started. Post: <see cref="IncomingDatagrams"/> is live.</remarks>
    Task StartAsync(IPAddress adapterIPv4, CancellationToken ct);

    /// <summary>
    /// Sends one M-SEARCH (<c>ST: upnp:rootdevice</c>) to <c>239.255.255.250:1900</c>
    /// via the ephemeral search socket, egressing on the bound adapter (FR-004 / FR-053 (a)).
    /// </summary>
    /// <param name="mx">Maximum response delay advertised in the <c>MX</c> header (clamped &#x2265; 1 s).</param>
    /// <param name="ct">Cancellation token for the send.</param>
    /// <remarks>Pre: <see cref="StartAsync"/> has completed.</remarks>
    Task SendMSearchAsync(TimeSpan mx, CancellationToken ct);

    /// <summary>
    /// The single-reader stream of received datagrams (Decision 2 bounded channel,
    /// <c>DropOldest(4096)</c>). The transport is producer-only; the consumer
    /// (<c>DiscoveryService</c>, Story 2.4) owns the read side.
    /// </summary>
    ChannelReader<SsdpDatagram> IncomingDatagrams { get; }
}
