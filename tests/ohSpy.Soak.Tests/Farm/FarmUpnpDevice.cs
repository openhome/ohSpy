namespace ohSpy.Soak.Tests.Farm;

using System.Globalization;
using System.Net.Http;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Story 6.2 — a single in-process farm device. An in-process Kestrel server bound to
/// <c>127.0.0.1:0</c> that serves a per-device <c>/description.xml</c> + <c>/scpd.xml</c> (canned or
/// GiantScpd), responds to GENA <c>SUBSCRIBE</c>/<c>RENEW</c>/<c>UNSUBSCRIBE</c>, answers SOAP
/// <c>POST /control</c> (success / SOAP-fault / timeout), and can POST GENA <c>NOTIFY</c> events
/// (including a truncated "partial NOTIFY") back to a callback host.
/// <para>
/// This BUILDS the capabilities the shipped HTTP-only 3-mode <c>FakeUpnpDevice</c> (Story 1.6)
/// deliberately deferred — per-device identity, GiantScpd, the GENA verbs, and the event emitter —
/// all soak-scoped (NEVER promoted to production). The base HTTP description/SCPD serving idea is
/// reused from the shipped fake's shape.
/// </para>
/// </summary>
internal sealed class FarmUpnpDevice : IAsyncDisposable
{
    private static readonly string[] SubscribeMethods = { "SUBSCRIBE" };
    private static readonly string[] UnsubscribeMethods = { "UNSUBSCRIBE" };

    private readonly DeviceSpec _spec;
    private readonly HttpClient _notifyClient = new();
    private WebApplication? _app;
    private Uri? _baseUrl;
    private int _subscribeSeq;
    private int _notifySeq;

    public FarmUpnpDevice(DeviceSpec spec) => _spec = spec;

    /// <summary>The opaque UDN body (after <c>uuid:</c>), unique per device.</summary>
    public string UdnBody => _spec.UdnBody;

    /// <summary>The full registry UDN (<c>uuid:&lt;body&gt;</c>).</summary>
    public string Udn => $"uuid:{_spec.UdnBody}";

    /// <summary>Absolute LOCATION URL the SSDP alive advertises (the device description endpoint).</summary>
    public Uri DescriptionUrl => new(_baseUrl ?? throw NotStarted(), "/description.xml");

    /// <summary>The most recent SID granted by a SUBSCRIBE (used to emit NOTIFY events to subscribers).</summary>
    public string? LastGrantedSid { get; private set; }

    private static InvalidOperationException NotStarted() =>
        new("FarmUpnpDevice not started — call StartAsync first.");

    public async Task StartAsync(CancellationToken ct = default)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseKestrel(opts => opts.Listen(System.Net.IPAddress.Loopback, 0));
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.None);

        _app = builder.Build();

        _app.MapGet("/description.xml", async ctx =>
        {
            ctx.Response.ContentType = "text/xml; charset=utf-8";
            await ctx.Response.WriteAsync(BuildDescriptionXml(), ctx.RequestAborted).ConfigureAwait(false);
        });

        _app.MapGet("/scpd.xml", async ctx =>
        {
            // Misbehaving "slow responder" reuse of the shipped hang semantics: on a slow device, dangle
            // the SCPD body forever (cancellable) so the cold-expand fetch hits its budget → timeout.
            if (_spec.Behavior == DeviceBehavior.SlowResponder)
            {
                await Task.Delay(Timeout.Infinite, ctx.RequestAborted).ConfigureAwait(false);
                return;
            }
            ctx.Response.ContentType = "text/xml; charset=utf-8";
            await ctx.Response.WriteAsync(BuildScpdXml(), ctx.RequestAborted).ConfigureAwait(false);
        });

        // GENA verbs on the eventSubURL.
        _app.MapMethods("/event", SubscribeMethods, HandleSubscribeAsync);
        _app.MapMethods("/event", UnsubscribeMethods, ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            return Task.CompletedTask;
        });

        // SOAP control endpoint.
        _app.MapPost("/control", HandleControlAsync);

        await _app.StartAsync(ct).ConfigureAwait(false);

        var server = _app.Services.GetRequiredService<IServer>();
        var feature = server.Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Kestrel did not expose IServerAddressesFeature.");
        var address = feature.Addresses.FirstOrDefault()
            ?? throw new InvalidOperationException("Kestrel bound zero addresses.");
        _baseUrl = new Uri(address);
    }

    private Task HandleSubscribeAsync(HttpContext ctx)
    {
        // Honour either CALLBACK (initial) or SID (renew). Grant a short lease so the soak's renew loop
        // actually fires within a compressed run; the client clamps to a MinRenewDelay floor anyway.
        var seq = Interlocked.Increment(ref _subscribeSeq);
        var sid = $"uuid:soak-sub-{_spec.UdnBody}-{seq.ToString(CultureInfo.InvariantCulture)}";
        LastGrantedSid = sid;
        ctx.Response.Headers["SID"] = sid;
        ctx.Response.Headers["TIMEOUT"] = "Second-1800";
        ctx.Response.StatusCode = StatusCodes.Status200OK;
        return Task.CompletedTask;
    }

    private async Task HandleControlAsync(HttpContext ctx)
    {
        switch (_spec.Behavior)
        {
            case DeviceBehavior.SlowResponder:
                // Dangle the SOAP response forever → the invocation hits its budget → timeout path.
                await Task.Delay(Timeout.Infinite, ctx.RequestAborted).ConfigureAwait(false);
                return;
            default:
                ctx.Response.ContentType = "text/xml; charset=utf-8";
                await ctx.Response.WriteAsync(SoapSuccessEnvelope, ctx.RequestAborted).ConfigureAwait(false);
                return;
        }
    }

    /// <summary>
    /// Emit a GENA NOTIFY to <paramref name="callbackBaseUrl"/> for the most recently granted SID.
    /// When <paramref name="partial"/> is true the body is TRUNCATED (the "partial NOTIFY" misbehaving
    /// case) — the callback host's body-read budget / parser must tolerate it without crashing.
    /// Best-effort: a transport error during a switch/teardown is swallowed (the soak asserts no
    /// UNHANDLED exception, and a failed NOTIFY POST is an expected transient).
    /// </summary>
    public async Task EmitNotifyAsync(Uri callbackBaseUrl, bool partial = false, CancellationToken ct = default)
    {
        var sid = LastGrantedSid;
        if (string.IsNullOrEmpty(sid))
        {
            return; // no live subscription yet
        }

        var seq = Interlocked.Increment(ref _notifySeq);
        var value = seq.ToString(CultureInfo.InvariantCulture);
        var body = partial
            ? "<e:propertyset xmlns:e=\"urn:schemas-upnp-org:event-1-0\"><e:property><Counter>" // truncated
            : $"<e:propertyset xmlns:e=\"urn:schemas-upnp-org:event-1-0\"><e:property><Counter>{value}</Counter></e:property></e:propertyset>";

        try
        {
            using var req = new HttpRequestMessage(new HttpMethod("NOTIFY"), callbackBaseUrl)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/xml"),
            };
            req.Headers.TryAddWithoutValidation("NT", "upnp:event");
            req.Headers.TryAddWithoutValidation("NTS", "upnp:propchange");
            req.Headers.TryAddWithoutValidation("SID", sid);
            req.Headers.TryAddWithoutValidation("SEQ", seq.ToString(CultureInfo.InvariantCulture));
            using var resp = await _notifyClient.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Callback host torn down / partial body rejected / transient — expected; not a soak failure.
        }
    }

    private string BuildDescriptionXml()
    {
        // One root device with one evented+controllable service. URLs are RELATIVE (the parser stores
        // them verbatim; the Core resolves them against LocationUrl — the production path).
        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <root xmlns="urn:schemas-upnp-org:device-1-0">
              <specVersion><major>1</major><minor>0</minor></specVersion>
              <device>
                <deviceType>urn:schemas-upnp-org:device:Basic:1</deviceType>
                <friendlyName>{_spec.FriendlyName}</friendlyName>
                <UDN>{Udn}</UDN>
                <manufacturer>ohSpy Soak Farm</manufacturer>
                <modelName>FarmUpnpDevice</modelName>
                <serviceList>
                  <service>
                    <serviceType>urn:schemas-upnp-org:service:SoakService:1</serviceType>
                    <serviceId>urn:upnp-org:serviceId:SoakService</serviceId>
                    <SCPDURL>/scpd.xml</SCPDURL>
                    <controlURL>/control</controlURL>
                    <eventSubURL>/event</eventSubURL>
                  </service>
                </serviceList>
              </device>
            </root>
            """;
    }

    private string BuildScpdXml()
    {
        if (_spec.Behavior != DeviceBehavior.GiantScpd)
        {
            return """
                <?xml version="1.0" encoding="UTF-8"?>
                <scpd xmlns="urn:schemas-upnp-org:service-1-0">
                  <specVersion><major>1</major><minor>0</minor></specVersion>
                  <actionList>
                    <action><name>Ping</name><argumentList/></action>
                  </actionList>
                  <serviceStateTable/>
                </scpd>
                """;
        }

        // GiantScpd: 100+ actions to exercise the FR-100 incremental stream + the cold-expand budget.
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        sb.Append("<scpd xmlns=\"urn:schemas-upnp-org:service-1-0\">\n");
        sb.Append("<specVersion><major>1</major><minor>0</minor></specVersion>\n<actionList>\n");
        for (var i = 0; i < 120; i++)
        {
            sb.Append("<action><name>Action");
            sb.Append(i.ToString(CultureInfo.InvariantCulture));
            sb.Append("</name><argumentList/></action>\n");
        }
        sb.Append("</actionList>\n<serviceStateTable/>\n</scpd>");
        return sb.ToString();
    }

    private const string SoapSuccessEnvelope =
        """
        <?xml version="1.0"?>
        <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/" s:encodingStyle="http://schemas.xmlsoap.org/soap/encoding/">
          <s:Body>
            <u:PingResponse xmlns:u="urn:schemas-upnp-org:service:SoakService:1"/>
          </s:Body>
        </s:Envelope>
        """;

    public async ValueTask DisposeAsync()
    {
        _notifyClient.Dispose();
        if (_app is not null)
        {
            using var shutdownCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await _app.StopAsync(shutdownCts.Token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { /* tolerate shutdown races */ }
            await _app.DisposeAsync().ConfigureAwait(false);
            _app = null;
        }
    }
}

/// <summary>Per-device farm behaviour mode.</summary>
internal enum DeviceBehavior
{
    /// <summary>Normal happy device — 200 OK description/SCPD, fast SOAP, healthy NOTIFY.</summary>
    Normal,

    /// <summary>Slow/hang responder — SCPD + SOAP dangle forever (cancellable) → cold-expand / invoke
    /// timeout path (reuses the shipped hang semantics rather than a bespoke slow-drip mode).</summary>
    SlowResponder,

    /// <summary>Larger-than-typical device — 120-action GiantScpd body (FR-100 incremental stream).</summary>
    GiantScpd,
}

/// <summary>Immutable per-device spec: unique identity + behaviour.</summary>
internal sealed record DeviceSpec(string UdnBody, string FriendlyName, DeviceBehavior Behavior);
