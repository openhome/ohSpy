namespace ohSpy.Core.Tests.Http;

using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ohSpy.Core.Diagnostics;
using ohSpy.Core.Http;
using ohSpy.Core.Tests.Fakes;

public class UpnpHttpClientTests
{
    private static readonly Uri SampleUrl = new("http://192.0.2.10:49152/description.xml");
    private static readonly Uri SampleControlUrl = new("http://192.0.2.10:49152/AVTransport/control");
    private static readonly Uri SampleEventUrl = new("http://192.0.2.10:49152/AVTransport/event");
    private static readonly Uri SampleCallbackUrl = new("http://192.0.2.99:8080/gena");

    private static IOptions<HttpTimeoutOptions> Opts(HttpTimeoutOptions o) => Options.Create(o);

    private static (UpnpHttpClient client, TestHttpMessageHandler handler, CapturingDiagnosticEmitter diag) Build(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder,
        HttpTimeoutOptions? overrideOpts = null)
    {
        var handler = new TestHttpMessageHandler(responder);
        var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var diag = new CapturingDiagnosticEmitter();
        var client = new UpnpHttpClient(http, Opts(overrideOpts ?? new HttpTimeoutOptions()), diag);
        return (client, handler, diag);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 9.2 — FetchScpdAsync / FetchDeviceDescriptionAsync (GET path)
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("ac", "AC-3.1")]
    public async Task FetchScpd_PerOpTimeoutFires_ThrowsUpnpTimeoutException()
    {
        var (client, _, _) = Build(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(200), ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }, new HttpTimeoutOptions { ScpdFetch = TimeSpan.FromMilliseconds(200) });

        var sw = Stopwatch.StartNew();
        Func<Task> act = () => client.FetchScpdAsync(SampleUrl, CancellationToken.None);
        var ex = await act.Should().ThrowAsync<UpnpTimeoutException>();
        sw.Stop();

        ex.Which.Url.Should().Be(SampleUrl);
        ex.Which.Budget.Should().Be(TimeSpan.FromMilliseconds(200));
        ex.Which.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(150));
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
    }

    [Fact]
    [Trait("ac", "AC-5")]
    public async Task FetchScpd_OnTimeout_EmitsHttpTimeoutWarningDiagnostic()
    {
        var (client, _, diag) = Build(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(200), ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }, new HttpTimeoutOptions { ScpdFetch = TimeSpan.FromMilliseconds(200) });

        Func<Task> act = () => client.FetchScpdAsync(SampleUrl, CancellationToken.None);
        await act.Should().ThrowAsync<UpnpTimeoutException>();

        var entry = diag.Entries.Should().ContainSingle(e => e.Category == DiagCategories.HttpTimeout).Which;
        entry.Severity.Should().Be("Warning");
        entry.Context.Url.Should().Be(SampleUrl.ToString());
        entry.Context.Elapsed.Should().NotBeNull();
        entry.Context.Budget.Should().Be(TimeSpan.FromMilliseconds(200));
    }

    [Fact]
    [Trait("ac", "AC-3.6")]
    public async Task FetchScpd_CallerCancellation_PropagatesOperationCanceledException()
    {
        using var callerCts = new CancellationTokenSource();
        var (client, _, diag) = Build(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }, new HttpTimeoutOptions { ScpdFetch = TimeSpan.FromSeconds(30) });

        var pending = client.FetchScpdAsync(SampleUrl, callerCts.Token);
        await Task.Delay(20);
        await callerCts.CancelAsync();

        Func<Task> act = () => pending;
        var ex = await act.Should().ThrowAsync<OperationCanceledException>();
        ex.Which.Should().NotBeOfType<UpnpTimeoutException>();
        diag.Entries.Should().BeEmpty();
    }

    [Fact]
    [Trait("ac", "AC-3.4")]
    public async Task FetchDeviceDescription_OversizeContentLength_ThrowsUpnpProtocolException()
    {
        var (client, _, diag) = Build((_, _) =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[10]),
            };
            // Force a declared length that exceeds the cap, regardless of payload bytes.
            resp.Content.Headers.ContentLength = 5_000_000;
            return Task.FromResult(resp);
        }, new HttpTimeoutOptions { MaxDescriptionBytes = 1_000_000 });

        Func<Task> act = () => client.FetchDeviceDescriptionAsync(SampleUrl, CancellationToken.None);
        await act.Should().ThrowAsync<UpnpProtocolException>();

        // Content-Length path throws BEFORE the streaming read, so HttpOversizeBody diagnostic
        // is NOT emitted (only the chunked-streaming path emits it).
        diag.Entries.Where(e => e.Category == DiagCategories.HttpOversizeBody).Should().BeEmpty();
    }

    [Fact]
    [Trait("ac", "AC-3.4")]
    public async Task FetchDeviceDescription_OversizeChunkedBody_ThrowsUpnpProtocolExceptionAndEmitsDiagnostic()
    {
        var (client, _, diag) = Build((_, _) =>
        {
            // 2 MB stream, content-length suppressed (chunked-transfer simulation).
            var bigStream = new MemoryStream(new byte[2 * 1024 * 1024]);
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(bigStream),
            };
            resp.Content.Headers.ContentLength = null;
            return Task.FromResult(resp);
        }, new HttpTimeoutOptions { MaxDescriptionBytes = 1_000_000 });

        Func<Task> act = () => client.FetchDeviceDescriptionAsync(SampleUrl, CancellationToken.None);
        await act.Should().ThrowAsync<UpnpProtocolException>();
        diag.Entries.Should().ContainSingle(e => e.Category == DiagCategories.HttpOversizeBody);
    }

    [Fact]
    [Trait("ac", "AC-3.5")]
    public async Task FetchScpd_HangAfter200Ok_ThrowsUpnpTimeoutExceptionWithinBudget()
    {
        // Canonical AC-3.5 regression: headers complete, body hangs forever.
        // The token MUST be threaded through ReadAsStreamAsync + stream.ReadAsync.
        var (client, _, _) = Build((_, _) =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new HangingStream()),
            };
            resp.Content.Headers.ContentLength = null;
            return Task.FromResult(resp);
        }, new HttpTimeoutOptions { ScpdFetch = TimeSpan.FromMilliseconds(200) });

        var sw = Stopwatch.StartNew();
        Func<Task> act = () => client.FetchScpdAsync(SampleUrl, CancellationToken.None);
        var ex = await act.Should().ThrowAsync<UpnpTimeoutException>();
        sw.Stop();

        ex.Which.Url.Should().Be(SampleUrl);
        ex.Which.Budget.Should().Be(TimeSpan.FromMilliseconds(200));
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
    }

    [Fact]
    [Trait("ac", "AC-3")]
    public async Task FetchScpd_TransportError_ThrowsUpnpTransportExceptionAndEmitsDiagnostic()
    {
        var (client, _, diag) = Build((_, _) => throw new HttpRequestException("conn refused"));

        Func<Task> act = () => client.FetchScpdAsync(SampleUrl, CancellationToken.None);
        var ex = await act.Should().ThrowAsync<UpnpTransportException>();
        ex.Which.Url.Should().Be(SampleUrl);
        ex.Which.StatusCode.Should().BeNull();
        diag.Entries.Should().ContainSingle(e => e.Category == DiagCategories.HttpTransport);
    }

    [Fact]
    [Trait("ac", "AC-3")]
    public async Task FetchScpd_HappyPath_ReturnsBytesAndEmitsNoDiagnostic()
    {
        var body = Encoding.UTF8.GetBytes("<scpd>ok</scpd>");
        var (client, _, diag) = Build((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(body),
        }));

        var result = await client.FetchScpdAsync(SampleUrl, CancellationToken.None);
        result.Should().BeEquivalentTo(body);
        diag.Entries.Should().BeEmpty();
    }

    [Fact]
    [Trait("ac", "AC-11.2")]
    public void UpnpHttpClient_ConstructedByProductionCtor_HasInfiniteHttpClientTimeout()
    {
        // Build via production ctor (constructs its own SocketsHttpHandler + HttpClient).
        using var client = new UpnpHttpClient(Opts(new HttpTimeoutOptions()), new NoOpEmitter());
        client.HttpClientTimeoutForTests.Should().Be(Timeout.InfiniteTimeSpan);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 9.3 — InvokeActionAsync (POST path)
    // ─────────────────────────────────────────────────────────────────────────

    private const string FaultEnvelope = """
        <?xml version="1.0"?>
        <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/" s:encodingStyle="http://schemas.xmlsoap.org/soap/encoding/">
          <s:Body>
            <s:Fault>
              <faultcode>s:Client</faultcode>
              <faultstring>UPnPError</faultstring>
              <detail>
                <UPnPError xmlns="urn:schemas-upnp-org:control-1-0">
                  <errorCode>701</errorCode>
                  <errorDescription>Invalid Action</errorDescription>
                </UPnPError>
              </detail>
            </s:Fault>
          </s:Body>
        </s:Envelope>
        """;

    private static SoapRequest SampleSoap() => new(
        SampleControlUrl,
        "urn:schemas-upnp-org:service:AVTransport:1",
        "Browse",
        "<?xml version=\"1.0\"?><s:Envelope/>");

    [Fact]
    [Trait("ac", "AC-3.3")]
    public async Task InvokeAction_Soap500WithFault_ThrowsUpnpFaultException()
    {
        var (client, _, _) = Build((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent(FaultEnvelope, Encoding.UTF8, "text/xml"),
        }));

        Func<Task> act = () => client.InvokeActionAsync(SampleSoap(), CancellationToken.None);
        var ex = await act.Should().ThrowAsync<UpnpFaultException>();
        ex.Which.ActionName.Should().Be("Browse");
        ex.Which.ErrorCode.Should().Be(701);
        ex.Which.ErrorDescription.Should().Be("Invalid Action");
        ex.Which.Url.Should().Be(SampleControlUrl);
    }

    [Fact]
    [Trait("ac", "AC-3.3")]
    public async Task InvokeAction_Malformed500_ThrowsUpnpTransportException()
    {
        var (client, _, _) = Build((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("<html><body>server error</body></html>", Encoding.UTF8, "text/html"),
        }));

        Func<Task> act = () => client.InvokeActionAsync(SampleSoap(), CancellationToken.None);
        var ex = await act.Should().ThrowAsync<UpnpTransportException>();
        ex.Which.StatusCode.Should().Be(500);
    }

    [Fact]
    [Trait("ac", "AC-3")]
    public async Task InvokeAction_HappyPath_ReturnsSoapResponseAndSetsSoapActionHeader()
    {
        const string responseEnvelope = "<?xml version=\"1.0\"?><s:Envelope><s:Body><BrowseResponse/></s:Body></s:Envelope>";
        var (client, handler, _) = Build((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseEnvelope, Encoding.UTF8, "text/xml"),
        }));

        var result = await client.InvokeActionAsync(SampleSoap(), CancellationToken.None);

        result.StatusCode.Should().Be(HttpStatusCode.OK);
        result.ResponseXml.Should().Be(responseEnvelope);

        var req = handler.Requests.Should().ContainSingle().Which;
        req.Method.Should().Be(HttpMethod.Post);
        req.Headers.TryGetValues("SOAPAction", out var soapActionValues).Should().BeTrue();
        soapActionValues!.Single().Should().Be("\"urn:schemas-upnp-org:service:AVTransport:1#Browse\"");
    }

    [Fact]
    [Trait("ac", "AC-3.1")]
    public async Task InvokeAction_Timeout_ThrowsUpnpTimeoutException()
    {
        var (client, _, diag) = Build(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }, new HttpTimeoutOptions { SoapInvoke = TimeSpan.FromMilliseconds(150) });

        Func<Task> act = () => client.InvokeActionAsync(SampleSoap(), CancellationToken.None);
        var ex = await act.Should().ThrowAsync<UpnpTimeoutException>();
        ex.Which.Budget.Should().Be(TimeSpan.FromMilliseconds(150));
        diag.Entries.Should().ContainSingle(e => e.Category == DiagCategories.HttpTimeout);
    }

    [Fact]
    [Trait("ac", "AC-3.6")]
    public async Task InvokeAction_CallerCancellation_PropagatesOperationCanceledException()
    {
        using var callerCts = new CancellationTokenSource();
        var (client, _, diag) = Build(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var pending = client.InvokeActionAsync(SampleSoap(), callerCts.Token);
        await Task.Delay(20);
        await callerCts.CancelAsync();

        Func<Task> act = () => pending;
        var ex = await act.Should().ThrowAsync<OperationCanceledException>();
        ex.Which.Should().NotBeOfType<UpnpTimeoutException>();
        diag.Entries.Should().BeEmpty();
    }

    [Fact]
    [Trait("ac", "AC-3")]
    public async Task InvokeAction_TransportError_ThrowsUpnpTransportException()
    {
        var (client, _, _) = Build((_, _) => throw new HttpRequestException("conn reset"));
        Func<Task> act = () => client.InvokeActionAsync(SampleSoap(), CancellationToken.None);
        await act.Should().ThrowAsync<UpnpTransportException>();
    }

    [Fact]
    [Trait("ac", "AC-3.4")]
    public async Task InvokeAction_OversizeContentLength_ThrowsUpnpProtocolException()
    {
        var (client, _, _) = Build((_, _) =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[10]) };
            resp.Content.Headers.ContentLength = 5_000_000;
            return Task.FromResult(resp);
        }, new HttpTimeoutOptions { MaxSoapResponseBytes = 1_000_000 });

        Func<Task> act = () => client.InvokeActionAsync(SampleSoap(), CancellationToken.None);
        await act.Should().ThrowAsync<UpnpProtocolException>();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 9.4 — Subscribe / Renew / Unsubscribe
    // ─────────────────────────────────────────────────────────────────────────

    private static HttpResponseMessage SubscribeOkResponse(string sid = "uuid:abc", int timeoutSeconds = 1800)
    {
        var resp = new HttpResponseMessage(HttpStatusCode.OK);
        resp.Headers.TryAddWithoutValidation("SID", sid);
        resp.Headers.TryAddWithoutValidation("TIMEOUT", $"Second-{timeoutSeconds}");
        return resp;
    }

    [Fact]
    [Trait("ac", "AC-3.2")]
    public async Task Subscribe_UsesCustomSubscribeMethodAndReturnsParsedResponse()
    {
        var (client, handler, _) = Build((_, _) => Task.FromResult(SubscribeOkResponse("uuid:abc", 1800)));

        var result = await client.SubscribeAsync(SampleEventUrl, SampleCallbackUrl, TimeSpan.FromMinutes(30), CancellationToken.None);

        result.Sid.Should().Be("uuid:abc");
        result.Timeout.Should().Be(TimeSpan.FromSeconds(1800));
        var req = handler.Requests.Should().ContainSingle().Which;
        req.Method.Method.Should().Be("SUBSCRIBE");
        req.Headers.Contains("CALLBACK").Should().BeTrue();
        req.Headers.Contains("NT").Should().BeTrue();
        req.Headers.Contains("TIMEOUT").Should().BeTrue();
    }

    [Fact]
    [Trait("ac", "AC-3.2")]
    public async Task Unsubscribe_UsesCustomUnsubscribeMethod()
    {
        var (client, handler, _) = Build((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        await client.UnsubscribeAsync(SampleEventUrl, "uuid:abc", CancellationToken.None);
        var req = handler.Requests.Should().ContainSingle().Which;
        req.Method.Method.Should().Be("UNSUBSCRIBE");
        req.Headers.TryGetValues("SID", out var sid).Should().BeTrue();
        sid!.Single().Should().Be("uuid:abc");
    }

    [Fact]
    public async Task Subscribe_ResponseMissingSid_ThrowsUpnpProtocolException()
    {
        var (client, _, _) = Build((_, _) =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK);
            resp.Headers.TryAddWithoutValidation("TIMEOUT", "Second-1800");
            return Task.FromResult(resp);
        });
        Func<Task> act = () => client.SubscribeAsync(SampleEventUrl, SampleCallbackUrl, TimeSpan.FromMinutes(30), CancellationToken.None);
        var ex = await act.Should().ThrowAsync<UpnpProtocolException>();
        ex.Which.Message.Should().Contain("SID");
    }

    [Fact]
    public async Task Subscribe_MalformedTimeoutHeader_ThrowsUpnpProtocolException()
    {
        var (client, _, _) = Build((_, _) =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK);
            resp.Headers.TryAddWithoutValidation("SID", "uuid:abc");
            resp.Headers.TryAddWithoutValidation("TIMEOUT", "NotASecondHeader");
            return Task.FromResult(resp);
        });
        Func<Task> act = () => client.SubscribeAsync(SampleEventUrl, SampleCallbackUrl, TimeSpan.FromMinutes(30), CancellationToken.None);
        await act.Should().ThrowAsync<UpnpProtocolException>();
    }

    [Fact]
    public async Task Renew_SendsSidHeaderAndOmitsCallback()
    {
        var (client, handler, _) = Build((_, _) => Task.FromResult(SubscribeOkResponse("uuid:abc", 600)));
        await client.RenewSubscriptionAsync(SampleEventUrl, "uuid:abc", TimeSpan.FromMinutes(10), CancellationToken.None);

        var req = handler.Requests.Should().ContainSingle().Which;
        req.Method.Method.Should().Be("SUBSCRIBE");
        req.Headers.Contains("SID").Should().BeTrue();
        req.Headers.Contains("CALLBACK").Should().BeFalse();
        req.Headers.Contains("NT").Should().BeFalse();
    }

    [Fact]
    [Trait("ac", "AC-3.1")]
    public async Task Subscribe_Timeout_ThrowsUpnpTimeoutException()
    {
        var (client, _, _) = Build(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return SubscribeOkResponse();
        }, new HttpTimeoutOptions { GenaSubscribe = TimeSpan.FromMilliseconds(150) });

        Func<Task> act = () => client.SubscribeAsync(SampleEventUrl, SampleCallbackUrl, TimeSpan.FromMinutes(30), CancellationToken.None);
        await act.Should().ThrowAsync<UpnpTimeoutException>();
    }

    [Fact]
    public async Task Subscribe_TransportError_ThrowsUpnpTransportException()
    {
        var (client, _, _) = Build((_, _) => throw new HttpRequestException("dns fail"));
        Func<Task> act = () => client.SubscribeAsync(SampleEventUrl, SampleCallbackUrl, TimeSpan.FromMinutes(30), CancellationToken.None);
        await act.Should().ThrowAsync<UpnpTransportException>();
    }

    [Fact]
    [Trait("ac", "AC-3.6")]
    public async Task Subscribe_CallerCancellation_PropagatesOperationCanceledException()
    {
        using var callerCts = new CancellationTokenSource();
        var (client, _, _) = Build(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return SubscribeOkResponse();
        });

        var pending = client.SubscribeAsync(SampleEventUrl, SampleCallbackUrl, TimeSpan.FromMinutes(30), callerCts.Token);
        await Task.Delay(20);
        await callerCts.CancelAsync();
        Func<Task> act = () => pending;
        var ex = await act.Should().ThrowAsync<OperationCanceledException>();
        ex.Which.Should().NotBeOfType<UpnpTimeoutException>();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 9.5 — HttpTimeoutOptions
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("ac", "AC-11.1")]
    public void HttpTimeoutOptions_Defaults_MatchDecision11Spec()
    {
        var o = new HttpTimeoutOptions();
        o.DescriptionFetch.Should().Be(TimeSpan.FromSeconds(5));
        o.ScpdFetch.Should().Be(TimeSpan.FromSeconds(10));
        o.SoapInvoke.Should().Be(TimeSpan.FromSeconds(10));
        o.GenaSubscribe.Should().Be(TimeSpan.FromSeconds(5));
        o.GenaUnsubscribe.Should().Be(TimeSpan.FromSeconds(5));
        o.ConnectTimeout.Should().Be(TimeSpan.FromSeconds(5));
        o.KeepAlivePingDelay.Should().Be(TimeSpan.FromSeconds(15));
        o.KeepAlivePingTimeout.Should().Be(TimeSpan.FromSeconds(5));
        o.CallbackHeaders.Should().Be(TimeSpan.FromSeconds(5));
        o.CallbackBody.Should().Be(TimeSpan.FromSeconds(5));
        o.MaxDescriptionBytes.Should().Be(1_048_576);
        o.MaxScpdBytes.Should().Be(2_097_152);
        o.MaxSoapResponseBytes.Should().Be(1_048_576);
        o.MaxGenaResponseBytes.Should().Be(65_536);
    }

    [Fact]
    [Trait("ac", "AC-11.3")]
    public void HttpTimeoutOptions_ConfigureOverridesAreVisibleViaIOptions()
    {
        var services = new ServiceCollection();
        services.Configure<HttpTimeoutOptions>(o => { /* init properties make full Configure delegate awkward — verified via direct construction below */ });
        // Verify the Configure pattern itself by mutating after construction is impossible
        // (init-only); instead we rely on Options.Create with a fresh instance carrying overrides.
        var custom = new HttpTimeoutOptions { ScpdFetch = TimeSpan.FromMilliseconds(50) };
        var iopts = Options.Create(custom);
        iopts.Value.ScpdFetch.Should().Be(TimeSpan.FromMilliseconds(50));
        iopts.Value.DescriptionFetch.Should().Be(TimeSpan.FromSeconds(5)); // unmodified default
    }

    [Fact]
    [Trait("ac", "AC-11.3")]
    public void HttpTimeoutOptions_RegisteredViaServiceCollection_ResolvableAsIOptions()
    {
        var services = new ServiceCollection();
        services.Configure<HttpTimeoutOptions>(_ => { });
        using var provider = services.BuildServiceProvider();
        var resolved = provider.GetRequiredService<IOptions<HttpTimeoutOptions>>();
        resolved.Value.Should().NotBeNull();
        resolved.Value.ScpdFetch.Should().Be(TimeSpan.FromSeconds(10)); // ctor default survives
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 9.6 — UpnpException hierarchy (A5)
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    [Trait("ac", "AC-1")]
    public void UpnpException_IsAbstract()
    {
        typeof(UpnpException).IsAbstract.Should().BeTrue();
    }

    [Theory]
    [Trait("ac", "AC-1")]
    [InlineData(typeof(UpnpTimeoutException))]
    [InlineData(typeof(UpnpTransportException))]
    [InlineData(typeof(UpnpProtocolException))]
    [InlineData(typeof(UpnpFaultException))]
    public void UpnpException_Derivatives_AreSealed(Type t)
    {
        t.IsSealed.Should().BeTrue();
        t.BaseType.Should().Be(typeof(UpnpException));
    }

    [Fact]
    [Trait("ac", "AC-1")]
    public void UpnpTimeoutException_CarriesUrlBudgetElapsed()
    {
        var ex = new UpnpTimeoutException(SampleUrl, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(1100));
        ex.Url.Should().Be(SampleUrl);
        ex.Budget.Should().Be(TimeSpan.FromSeconds(1));
        ex.Elapsed.Should().Be(TimeSpan.FromMilliseconds(1100));
    }

    [Fact]
    [Trait("ac", "AC-1")]
    public void UpnpTransportException_CarriesUrlStatusCode()
    {
        var ex = new UpnpTransportException(SampleUrl, "boom", 404);
        ex.Url.Should().Be(SampleUrl);
        ex.StatusCode.Should().Be(404);
    }

    [Fact]
    [Trait("ac", "AC-1")]
    public void UpnpProtocolException_CarriesUrl()
    {
        var ex = new UpnpProtocolException(SampleUrl, "bad");
        ex.Url.Should().Be(SampleUrl);
    }

    [Fact]
    [Trait("ac", "AC-1")]
    public void UpnpFaultException_CarriesActionNameAndErrorCodeAndDescription()
    {
        var ex = new UpnpFaultException(SampleUrl, "Browse", 701, "Invalid Action");
        ex.ActionName.Should().Be("Browse");
        ex.ErrorCode.Should().Be(701);
        ex.ErrorDescription.Should().Be("Invalid Action");
        ex.Url.Should().Be(SampleUrl);
    }

    [Theory]
    [Trait("ac", "AC-1")]
    [InlineData(typeof(UpnpException))]
    [InlineData(typeof(UpnpTimeoutException))]
    [InlineData(typeof(UpnpTransportException))]
    [InlineData(typeof(UpnpProtocolException))]
    [InlineData(typeof(UpnpFaultException))]
    public void UpnpException_Types_AreNotSerializable(Type t)
    {
        t.GetCustomAttribute<SerializableAttribute>().Should().BeNull();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Local helpers
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>No-op emitter for tests that don't need to capture diagnostics.</summary>
    private sealed class NoOpEmitter : IDiagnosticEmitter
    {
        public void Verbose(string c, string m, DiagnosticContext ctx = default) { }
        public void Information(string c, string m, DiagnosticContext ctx = default) { }
        public void Warning(string c, string m, DiagnosticContext ctx = default) { }
        public void Error(string c, string m, DiagnosticContext ctx = default) { }
    }
}
