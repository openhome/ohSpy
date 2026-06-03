namespace ohSpy.Core.Http;

using System.Diagnostics;
using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using ohSpy.Core.Diagnostics;
using ohSpy.Core.Models;
using ohSpy.Core.Soap;

/// <summary>
/// Production implementation of <see cref="IUpnpHttpClient"/>. Owns a single shared
/// <see cref="HttpClient"/> over a configured <see cref="SocketsHttpHandler"/>.
/// All per-op timeouts are enforced via linked <see cref="CancellationTokenSource"/>
/// (NOT <see cref="HttpClient.Timeout"/>, which is set to infinite).
/// </summary>
internal sealed class UpnpHttpClient : IUpnpHttpClient, IDisposable
{
    private readonly HttpClient _http;
    private readonly HttpTimeoutOptions _opts;
    private readonly IDiagnosticEmitter _diag;

    public UpnpHttpClient(IOptions<HttpTimeoutOptions> options, IDiagnosticEmitter diag)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(diag);
        _opts = options.Value;
        _diag = diag;

        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            ConnectTimeout = _opts.ConnectTimeout,
            KeepAlivePingDelay = _opts.KeepAlivePingDelay,
            KeepAlivePingTimeout = _opts.KeepAlivePingTimeout,
            MaxResponseHeadersLength = 16,                    // 16 KB
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        };
        _http = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,               // SOLE timeout = per-op linked CTS
            DefaultRequestVersion = HttpVersion.Version11,
        };
    }

    // Test-only ctor — accepts a pre-built HttpClient (typically over TestHttpMessageHandler).
    internal UpnpHttpClient(HttpClient httpForTests, IOptions<HttpTimeoutOptions> options, IDiagnosticEmitter diag)
    {
        ArgumentNullException.ThrowIfNull(httpForTests);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(diag);
        _http = httpForTests;
        _opts = options.Value;
        _diag = diag;
    }

    // Test-only accessor — lets AC-11.2 assert _http.Timeout == InfiniteTimeSpan
    // without resorting to reflection. Exposed only via InternalsVisibleTo.
    internal TimeSpan HttpClientTimeoutForTests => _http.Timeout;

    public Task<byte[]> FetchDeviceDescriptionAsync(Uri locationUrl, CancellationToken ct) =>
        GetBytesWithSizeCapAsync(locationUrl, _opts.DescriptionFetch, _opts.MaxDescriptionBytes, ct);

    public Task<byte[]> FetchScpdAsync(Uri scpdUrl, CancellationToken ct) =>
        GetBytesWithSizeCapAsync(scpdUrl, _opts.ScpdFetch, _opts.MaxScpdBytes, ct);

    // --- shared GET implementation ---
    private async Task<byte[]> GetBytesWithSizeCapAsync(
        Uri url, TimeSpan budget, int maxBytes, CancellationToken external)
    {
        ArgumentNullException.ThrowIfNull(url);
        using var timeoutCts = new CancellationTokenSource(budget);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(external, timeoutCts.Token);

        var sw = Stopwatch.StartNew();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var resp = await _http.SendAsync(
                req, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);

            EnforceSizeCapOnHeaders(resp, url, maxBytes);
            var bytes = await ReadWithSizeCapAsync(resp, maxBytes, linked.Token).ConfigureAwait(false);
            return bytes;
        }
        catch (OperationCanceledException) when (external.IsCancellationRequested)
        {
            throw;                                            // caller cancelled: silent re-throw
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            _diag.Warning(DiagCategories.HttpTimeout, "request timed out",
                new DiagnosticContext { Url = url.ToString(), Elapsed = sw.Elapsed, Budget = budget });
            throw new UpnpTimeoutException(url, budget, sw.Elapsed);
        }
        catch (HttpRequestException ex)
        {
            _diag.Warning(DiagCategories.HttpTransport, ex.Message,
                new DiagnosticContext { Url = url.ToString(), StatusCode = (int?)ex.StatusCode });
            throw new UpnpTransportException(url, ex.Message, (int?)ex.StatusCode, ex);
        }
    }

    // Throws UpnpProtocolException + disposes resp if Content-Length already exceeds cap.
    private static void EnforceSizeCapOnHeaders(HttpResponseMessage resp, Uri url, int maxBytes)
    {
        var len = resp.Content.Headers.ContentLength;
        if (len.HasValue && len.Value > maxBytes)
        {
            resp.Dispose();
            throw new UpnpProtocolException(url,
                $"response body declared {len.Value} bytes; per-method cap is {maxBytes}");
        }
    }

    // Streaming size guard: throws UpnpProtocolException if cumulative bytes exceed cap.
    // Handles chunked transfer (null Content-Length) — the only safe way to enforce caps
    // when the server doesn't declare length up front.
    private async Task<byte[]> ReadWithSizeCapAsync(HttpResponseMessage resp, int maxBytes, CancellationToken ct)
    {
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        long total = 0;
        while ((read = await stream.ReadAsync(chunk.AsMemory(), ct).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > maxBytes)
            {
                _diag.Warning(DiagCategories.HttpOversizeBody, "body exceeded per-method cap",
                    new DiagnosticContext { Url = resp.RequestMessage?.RequestUri?.ToString() });
                throw new UpnpProtocolException(
                    resp.RequestMessage?.RequestUri ?? new Uri("about:blank"),
                    $"response body exceeded {maxBytes} bytes mid-read");
            }
            await buffer.WriteAsync(chunk.AsMemory(0, read), ct).ConfigureAwait(false);
        }
        return buffer.ToArray();
    }

    public async Task<SoapResponse> InvokeActionAsync(SoapRequest request, CancellationToken external)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var timeoutCts = new CancellationTokenSource(_opts.SoapInvoke);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(external, timeoutCts.Token);
        var sw = Stopwatch.StartNew();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, request.ControlUrl)
            {
                Content = new StringContent(SoapEnvelopeBuilder.Build(request), Encoding.UTF8, "text/xml"),
            };
            // SOAPAction MUST be quoted: "urn:..#ActionName"
            req.Headers.TryAddWithoutValidation("SOAPAction", $"\"{request.ServiceType}#{request.ActionName}\"");

            using var resp = await _http.SendAsync(
                req, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);

            EnforceSizeCapOnHeaders(resp, request.ControlUrl, _opts.MaxSoapResponseBytes);
            var bytes = await ReadWithSizeCapAsync(resp, _opts.MaxSoapResponseBytes, linked.Token).ConfigureAwait(false);

            if (resp.StatusCode == HttpStatusCode.InternalServerError)
            {
                // SOAP fault path — try to parse <s:Fault><detail><UPnPError><errorCode/></UPnPError></detail>.
                // Emit diagnostics before throw (these paths bypass the catch-block diagnostic since
                // the throw originates inside try; UpnpFault/UpnpTransport are not HttpRequestException).
                // DeviceUuid is absent at this layer (SoapRequest carries no uuid) — Story 3.2's popup
                // VM emits a second, UUID-bearing SoapFault diagnostic at its catch site.
                if (SoapFaultParser.TryParse(bytes, out var fault))
                {
                    _diag.Warning(DiagCategories.SoapFault, "SOAP action returned a UPnP fault",
                        new DiagnosticContext
                        {
                            Url = request.ControlUrl.ToString(),
                            ActionName = request.ActionName,
                            StatusCode = 500,
                            ErrorText = $"{fault.ErrorCode}: {fault.ErrorDescription}",
                        });
                    throw new UpnpFaultException(request.ControlUrl, request.ActionName,
                        fault.ErrorCode, fault.ErrorDescription);
                }
                _diag.Warning(DiagCategories.SoapInvoke, "HTTP 500 without parseable UPnPError",
                    new DiagnosticContext { Url = request.ControlUrl.ToString(), ActionName = request.ActionName, StatusCode = 500 });
                throw new UpnpTransportException(request.ControlUrl,
                    "HTTP 500 without parseable UPnPError", 500);
            }
            if (!resp.IsSuccessStatusCode)
            {
                _diag.Warning(DiagCategories.SoapInvoke, $"unexpected status {(int)resp.StatusCode}",
                    new DiagnosticContext { Url = request.ControlUrl.ToString(), ActionName = request.ActionName, StatusCode = (int)resp.StatusCode });
                throw new UpnpTransportException(request.ControlUrl,
                    $"unexpected status {(int)resp.StatusCode}", (int)resp.StatusCode);
            }
            try
            {
                return new SoapResponse(request.ActionName, SoapResponseReader.ReadOutputArguments(bytes));
            }
            catch (System.Xml.XmlException ex)
            {
                // 2xx with unparseable body — surface as a protocol error + diagnostic so the
                // caller always gets a typed exception. (SoapResponseReader throws XmlException;
                // without this catch it would escape all InvokeActionAsync catch blocks.)
                _diag.Warning(DiagCategories.SoapInvoke, "SOAP response body could not be parsed",
                    new DiagnosticContext { Url = request.ControlUrl.ToString(), ActionName = request.ActionName, ErrorText = ex.Message });
                throw new UpnpProtocolException(request.ControlUrl,
                    $"SOAP response body could not be parsed: {ex.Message}");
            }
        }
        catch (OperationCanceledException) when (external.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            sw.Stop();
            _diag.Warning(DiagCategories.HttpTimeout, "SOAP invoke timed out",
                new DiagnosticContext { Url = request.ControlUrl.ToString(), ActionName = request.ActionName,
                                         Elapsed = sw.Elapsed, Budget = _opts.SoapInvoke });
            throw new UpnpTimeoutException(request.ControlUrl, _opts.SoapInvoke, sw.Elapsed);
        }
        catch (HttpRequestException ex)
        {
            _diag.Warning(DiagCategories.HttpTransport, ex.Message,
                new DiagnosticContext { Url = request.ControlUrl.ToString(), ActionName = request.ActionName,
                                         StatusCode = (int?)ex.StatusCode });
            throw new UpnpTransportException(request.ControlUrl, ex.Message, (int?)ex.StatusCode, ex);
        }
    }

    public Task<SubscribeResponse> SubscribeAsync(
        Uri eventSubUrl, Uri callbackUrl, TimeSpan requestedTimeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(eventSubUrl);
        ArgumentNullException.ThrowIfNull(callbackUrl);
        var headers = new[]
        {
            ("CALLBACK", $"<{callbackUrl}>"),
            ("NT", "upnp:event"),
            ("TIMEOUT", $"Second-{(int)requestedTimeout.TotalSeconds}"),
        };
        return SendSubscribeOrRenewAsync(eventSubUrl, headers, ct);
    }

    public Task<SubscribeResponse> RenewSubscriptionAsync(
        Uri eventSubUrl, string sid, TimeSpan requestedTimeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(eventSubUrl);
        ArgumentException.ThrowIfNullOrEmpty(sid);
        var headers = new[]
        {
            ("SID", sid),
            ("TIMEOUT", $"Second-{(int)requestedTimeout.TotalSeconds}"),
        };
        return SendSubscribeOrRenewAsync(eventSubUrl, headers, ct);
    }

    private async Task<SubscribeResponse> SendSubscribeOrRenewAsync(
        Uri eventSubUrl, (string Name, string Value)[] headers, CancellationToken external)
    {
        using var timeoutCts = new CancellationTokenSource(_opts.GenaSubscribe);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(external, timeoutCts.Token);
        var sw = Stopwatch.StartNew();
        try
        {
            using var req = new HttpRequestMessage(new HttpMethod("SUBSCRIBE"), eventSubUrl);
            foreach (var (name, value) in headers)
                req.Headers.TryAddWithoutValidation(name, value);

            using var resp = await _http.SendAsync(
                req, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
            EnforceSizeCapOnHeaders(resp, eventSubUrl, _opts.MaxGenaResponseBytes);

            if (!resp.IsSuccessStatusCode)
            {
                throw new UpnpTransportException(eventSubUrl,
                    $"SUBSCRIBE returned {(int)resp.StatusCode}", (int)resp.StatusCode);
            }
            if (!resp.Headers.TryGetValues("SID", out var sidValues))
                throw new UpnpProtocolException(eventSubUrl, "SUBSCRIBE response missing SID header");
            if (!resp.Headers.TryGetValues("TIMEOUT", out var timeoutValues))
                throw new UpnpProtocolException(eventSubUrl, "SUBSCRIBE response missing TIMEOUT header");

            var sid = sidValues.First();
            var granted = ParseSecondHeader(timeoutValues.First())
                ?? throw new UpnpProtocolException(eventSubUrl,
                    $"SUBSCRIBE response TIMEOUT header malformed: '{timeoutValues.First()}'");

            return new SubscribeResponse(sid, granted);
        }
        catch (OperationCanceledException) when (external.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            sw.Stop();
            _diag.Warning(DiagCategories.HttpTimeout, "SUBSCRIBE/RENEW timed out",
                new DiagnosticContext { Url = eventSubUrl.ToString(), Elapsed = sw.Elapsed, Budget = _opts.GenaSubscribe });
            throw new UpnpTimeoutException(eventSubUrl, _opts.GenaSubscribe, sw.Elapsed);
        }
        catch (HttpRequestException ex)
        {
            _diag.Warning(DiagCategories.HttpTransport, ex.Message,
                new DiagnosticContext { Url = eventSubUrl.ToString(), StatusCode = (int?)ex.StatusCode });
            throw new UpnpTransportException(eventSubUrl, ex.Message, (int?)ex.StatusCode, ex);
        }
    }

    // Parses "Second-N" (the only legitimate UPnP TIMEOUT shape v1 supports).
    // Returns null on malformed input; "Second-infinite" is also returned as null
    // (caller decides whether to treat as "never expires" — for ohSpy we treat it
    // as an unsupported edge case and surface UpnpProtocolException above).
    private static TimeSpan? ParseSecondHeader(string value)
    {
        const string prefix = "Second-";
        if (string.IsNullOrEmpty(value) || !value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;
        var rest = value[prefix.Length..];
        return int.TryParse(rest, out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : null;
    }

    public async Task UnsubscribeAsync(Uri eventSubUrl, string sid, CancellationToken external)
    {
        ArgumentNullException.ThrowIfNull(eventSubUrl);
        ArgumentException.ThrowIfNullOrEmpty(sid);
        using var timeoutCts = new CancellationTokenSource(_opts.GenaUnsubscribe);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(external, timeoutCts.Token);
        var sw = Stopwatch.StartNew();
        try
        {
            using var req = new HttpRequestMessage(new HttpMethod("UNSUBSCRIBE"), eventSubUrl);
            req.Headers.TryAddWithoutValidation("SID", sid);
            using var resp = await _http.SendAsync(
                req, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                throw new UpnpTransportException(eventSubUrl,
                    $"UNSUBSCRIBE returned {(int)resp.StatusCode}", (int)resp.StatusCode);
            }
        }
        catch (OperationCanceledException) when (external.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            sw.Stop();
            _diag.Warning(DiagCategories.HttpTimeout, "UNSUBSCRIBE timed out",
                new DiagnosticContext { Url = eventSubUrl.ToString(), Sid = sid, Elapsed = sw.Elapsed, Budget = _opts.GenaUnsubscribe });
            throw new UpnpTimeoutException(eventSubUrl, _opts.GenaUnsubscribe, sw.Elapsed);
        }
        catch (HttpRequestException ex)
        {
            _diag.Warning(DiagCategories.HttpTransport, ex.Message,
                new DiagnosticContext { Url = eventSubUrl.ToString(), Sid = sid, StatusCode = (int?)ex.StatusCode });
            throw new UpnpTransportException(eventSubUrl, ex.Message, (int?)ex.StatusCode, ex);
        }
    }

    public void Dispose() => _http.Dispose();
}
