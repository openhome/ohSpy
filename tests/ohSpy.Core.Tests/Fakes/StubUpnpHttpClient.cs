namespace ohSpy.Core.Tests.Fakes;

using ohSpy.Core.Http;

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

    public Task<SoapResponse> InvokeActionAsync(SoapRequest request, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<SubscribeResponse> SubscribeAsync(
        Uri eventSubUrl, Uri callbackUrl, TimeSpan requestedTimeout, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<SubscribeResponse> RenewSubscriptionAsync(
        Uri eventSubUrl, string sid, TimeSpan requestedTimeout, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task UnsubscribeAsync(Uri eventSubUrl, string sid, CancellationToken ct) =>
        throw new NotSupportedException();
}
