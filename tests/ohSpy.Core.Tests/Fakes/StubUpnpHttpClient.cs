namespace ohSpy.Core.Tests.Fakes;

using ohSpy.Core.Http;
using ohSpy.Core.Models;

/// <summary>
/// Test double for <see cref="IUpnpHttpClient"/> covering only the device-description
/// path Story 2.3 exercises. The <see cref="DescriptionResponder"/> closure gives each
/// test full control (return canned bytes, throw, or block on a token/gate). Records the
/// requested URLs and tracks peak concurrent in-flight calls (NFR-P6 assertion).
/// </summary>
internal sealed class StubUpnpHttpClient : IUpnpHttpClient
{
    private readonly object _gate = new();
    private readonly List<Uri> _requested = new();
    private int _inFlight;

    /// <summary>URLs passed to <see cref="FetchDeviceDescriptionAsync"/>, in call order.</summary>
    public IReadOnlyList<Uri> RequestedUrls
    {
        get { lock (_gate) { return _requested.ToArray(); } }
    }

    /// <summary>Highest number of concurrently in-flight description fetches observed.</summary>
    public int PeakConcurrency { get; private set; }

    /// <summary>Supplies the description-fetch result. Default: empty bytes.</summary>
    public Func<Uri, CancellationToken, Task<byte[]>> DescriptionResponder { get; set; } =
        (_, _) => Task.FromResult(Array.Empty<byte>());

    public async Task<byte[]> FetchDeviceDescriptionAsync(Uri locationUrl, CancellationToken ct)
    {
        lock (_gate)
        {
            _requested.Add(locationUrl);
            _inFlight++;
            if (_inFlight > PeakConcurrency)
            {
                PeakConcurrency = _inFlight;
            }
        }

        try
        {
            return await DescriptionResponder(locationUrl, ct).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _inFlight--;
            }
        }
    }

    /// <summary>Supplies the SCPD-fetch result. Default: throws NotSupportedException so
    /// tests that don't opt in still fail loudly if the path is hit unexpectedly.</summary>
    public Func<Uri, CancellationToken, Task<byte[]>>? ScpdResponder { get; set; }

    public async Task<byte[]> FetchScpdAsync(Uri scpdUrl, CancellationToken ct)
    {
        if (ScpdResponder is null) throw new NotSupportedException();
        lock (_gate) { _requested.Add(scpdUrl); }
        return await ScpdResponder(scpdUrl, ct).ConfigureAwait(false);
    }

    private readonly List<SoapRequest> _invoked = new();

    /// <summary>SOAP requests passed to <see cref="InvokeActionAsync"/>, in call order. Tests assert
    /// on the request that WENT OUT (resolved absolute ControlUrl, args 1:1) — Epic 2 lesson.</summary>
    public IReadOnlyList<SoapRequest> InvokedRequests
    {
        get { lock (_gate) { return _invoked.ToArray(); } }
    }

    /// <summary>Supplies the invoke result. Default: throws NotSupportedException so tests that don't
    /// opt in fail loudly if the path is hit. A test can return a <see cref="SoapResponse"/>, throw a
    /// typed UpnpException, or block on the token (await Task.Delay(Infinite, ct)) to test cancel.</summary>
    public Func<SoapRequest, CancellationToken, Task<SoapResponse>>? InvokeResponder { get; set; }

    public async Task<SoapResponse> InvokeActionAsync(SoapRequest request, CancellationToken ct)
    {
        lock (_gate) { _invoked.Add(request); }
        if (InvokeResponder is null) throw new NotSupportedException();
        return await InvokeResponder(request, ct).ConfigureAwait(false);
    }

    // ── Story 4.2 — controllable GENA verbs (mirror InvokeResponder/InvokedRequests) ──

    /// <summary>One recorded GENA call (the request that WENT OUT — Epic 2 "assert the outbound" lesson).</summary>
    public sealed record GenaCall(string Verb, Uri EventSubUrl, Uri? CallbackUrl, string? Sid, TimeSpan RequestedTimeout);

    private readonly List<GenaCall> _genaCalls = new();

    /// <summary>Every SUBSCRIBE/RENEW/UNSUBSCRIBE call, in order.</summary>
    public IReadOnlyList<GenaCall> GenaCalls
    {
        get { lock (_gate) { return _genaCalls.ToArray(); } }
    }

    public int CountOf(string verb)
    {
        lock (_gate) { return _genaCalls.Count(c => c.Verb == verb); }
    }

    /// <summary>Supplies the SUBSCRIBE result. Default: throws so opt-out tests fail loudly.</summary>
    public Func<Uri, Uri, TimeSpan, CancellationToken, Task<SubscribeResponse>>? SubscribeResponder { get; set; }

    /// <summary>Supplies the RENEW result. Default: throws so opt-out tests fail loudly.</summary>
    public Func<Uri, string, TimeSpan, CancellationToken, Task<SubscribeResponse>>? RenewResponder { get; set; }

    /// <summary>Supplies the UNSUBSCRIBE behaviour. Default: completes (best-effort success).</summary>
    public Func<Uri, string, CancellationToken, Task>? UnsubscribeResponder { get; set; }

    public async Task<SubscribeResponse> SubscribeAsync(
        Uri eventSubUrl, Uri callbackUrl, TimeSpan requestedTimeout, CancellationToken ct)
    {
        lock (_gate) { _genaCalls.Add(new GenaCall("SUBSCRIBE", eventSubUrl, callbackUrl, null, requestedTimeout)); }
        if (SubscribeResponder is null) throw new NotSupportedException();
        return await SubscribeResponder(eventSubUrl, callbackUrl, requestedTimeout, ct).ConfigureAwait(false);
    }

    public async Task<SubscribeResponse> RenewSubscriptionAsync(
        Uri eventSubUrl, string sid, TimeSpan requestedTimeout, CancellationToken ct)
    {
        lock (_gate) { _genaCalls.Add(new GenaCall("RENEW", eventSubUrl, null, sid, requestedTimeout)); }
        if (RenewResponder is null) throw new NotSupportedException();
        return await RenewResponder(eventSubUrl, sid, requestedTimeout, ct).ConfigureAwait(false);
    }

    public async Task UnsubscribeAsync(Uri eventSubUrl, string sid, CancellationToken ct)
    {
        lock (_gate) { _genaCalls.Add(new GenaCall("UNSUBSCRIBE", eventSubUrl, null, sid, TimeSpan.Zero)); }
        if (UnsubscribeResponder is null) return;
        await UnsubscribeResponder(eventSubUrl, sid, ct).ConfigureAwait(false);
    }
}
