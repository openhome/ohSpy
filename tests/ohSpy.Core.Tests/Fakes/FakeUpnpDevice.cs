namespace ohSpy.Core.Tests.Fakes;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// In-process Kestrel server bound to <c>127.0.0.1:0</c> (OS-assigned ephemeral port)
/// that responds to GET requests for <see cref="DescriptionUrl"/> and
/// <see cref="ScpdUrl"/> according to the <see cref="FakeUpnpDeviceBehavior"/>
/// passed at construction.
/// <para>
/// One fixture per test — no sharing. Tests instantiate, exercise, dispose.
/// Port collisions are impossible because each fixture binds to port 0
/// (kernel assigns a unique free port).
/// </para>
/// </summary>
internal sealed class FakeUpnpDevice : IAsyncDisposable
{
    // Canned bodies. Just enough to be valid HTTP responses; not exercised by
    // Story 1.6's chaos test (which intentionally never reads the SCPD body
    // to completion).
    private const string DescriptionXml =
        """
        <?xml version="1.0" encoding="UTF-8"?>
        <root xmlns="urn:schemas-upnp-org:device-1-0">
          <specVersion><major>1</major><minor>0</minor></specVersion>
          <device>
            <deviceType>urn:schemas-upnp-org:device:Basic:1</deviceType>
            <friendlyName>FakeUpnpDevice</friendlyName>
            <UDN>uuid:fake-device-0000-0000-000000000001</UDN>
            <manufacturer>ohSpy Tests</manufacturer>
            <modelName>FakeUpnpDevice</modelName>
          </device>
        </root>
        """;

    private const string ScpdXml =
        """
        <?xml version="1.0" encoding="UTF-8"?>
        <scpd xmlns="urn:schemas-upnp-org:service-1-0">
          <specVersion><major>1</major><minor>0</minor></specVersion>
          <actionList/>
          <serviceStateTable/>
        </scpd>
        """;

    private readonly FakeUpnpDeviceBehavior _behavior;
    private WebApplication? _app;
    private Uri? _baseUrl;

    public FakeUpnpDevice(FakeUpnpDeviceBehavior behavior)
    {
        _behavior = behavior;
    }

    /// <summary>Absolute URL the description-fetch test points at.</summary>
    public Uri DescriptionUrl => new(_baseUrl ?? throw NotStarted(), "/description.xml");

    /// <summary>Absolute URL the SCPD-fetch test points at.</summary>
    public Uri ScpdUrl => new(_baseUrl ?? throw NotStarted(), "/scpd.xml");

    private static InvalidOperationException NotStarted() =>
        new("FakeUpnpDevice not started — call StartAsync first.");

    /// <summary>
    /// Spin up the Kestrel host on 127.0.0.1:0. After the call returns,
    /// <see cref="DescriptionUrl"/> and <see cref="ScpdUrl"/> are usable.
    /// </summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        // CreateSlimBuilder: no JSON config, no env-var scanning, no appsettings.json
        // resolution. ~30% faster startup, no test-fixture pollution from env vars.
        var builder = WebApplication.CreateSlimBuilder();

        // Bind to 127.0.0.1 on ephemeral port. IPAddress.Loopback is IPv4 — avoids
        // dual-stack confusion.
        builder.WebHost.UseKestrel(opts =>
        {
            opts.Listen(System.Net.IPAddress.Loopback, 0);
        });

        // Silence Kestrel's verbose startup logging ("Now listening on...", etc.)
        // xUnit captures stdout per-test and merges noisily.
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.None);

        _app = builder.Build();

        _app.MapGet("/description.xml", HandleAsync);
        _app.MapGet("/scpd.xml", HandleAsync);

        await _app.StartAsync(ct).ConfigureAwait(false);

        // After Start, the server addresses are populated. Capture the URL.
        var server = _app.Services.GetRequiredService<IServer>();
        var feature = server.Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Kestrel did not expose IServerAddressesFeature.");
        var address = feature.Addresses.FirstOrDefault()
            ?? throw new InvalidOperationException("Kestrel bound zero addresses.");
        _baseUrl = new Uri(address);
    }

    private async Task HandleAsync(HttpContext ctx)
    {
        // Use the request URL to decide which canned body to use; the response shape
        // depends on the configured behavior.
        var body = ctx.Request.Path.Value?.EndsWith("scpd.xml", StringComparison.Ordinal) == true
            ? ScpdXml
            : DescriptionXml;

        switch (_behavior)
        {
            case FakeUpnpDeviceBehavior.Happy:
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "text/xml; charset=utf-8";
                await ctx.Response.WriteAsync(body, ctx.RequestAborted).ConfigureAwait(false);
                return;

            case FakeUpnpDeviceBehavior.HangBeforeHeaders:
                // Accept the request; never send headers. The await Task.Delay honours
                // request-abort so disposal cancels cleanly.
                await Task.Delay(Timeout.Infinite, ctx.RequestAborted).ConfigureAwait(false);
                return;

            case FakeUpnpDeviceBehavior.HangAfter200Ok:
                // Send 200 + headers immediately, with a Content-Length large enough
                // that the client will wait for body bytes. Then await forever on the
                // body-write side — the body bytes never arrive.
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "text/xml; charset=utf-8";
                ctx.Response.ContentLength = body.Length;
                // CRITICAL: Response.StartAsync is the canonical Kestrel API for
                // "send the response prelude (status + headers) now, hold the body
                // open." Body.FlushAsync on an empty body does NOT reliably flush
                // headers — Kestrel can hold them until the first body write. Without
                // StartAsync the client never transitions from header-wait to
                // body-read, defeating the AC-3.5 scenario.
                await ctx.Response.StartAsync(ctx.RequestAborted).ConfigureAwait(false);
                // Now block forever on the body. Cancellable so dispose returns
                // cleanly when the test ends.
                await Task.Delay(Timeout.Infinite, ctx.RequestAborted).ConfigureAwait(false);
                return;

            default:
                ctx.Response.StatusCode = 500;
                await ctx.Response.WriteAsync("FakeUpnpDevice: unrecognised behavior", ctx.RequestAborted)
                                   .ConfigureAwait(false);
                return;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            using var shutdownCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await _app.StopAsync(shutdownCts.Token).ConfigureAwait(false); }
            catch { /* tolerate shutdown races */ }
            await _app.DisposeAsync().ConfigureAwait(false);
            _app = null;
        }
    }
}
