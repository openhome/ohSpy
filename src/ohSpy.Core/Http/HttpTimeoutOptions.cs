namespace ohSpy.Core.Http;

/// <summary>
/// Per-request timeout budgets and response-body size caps for <see cref="IUpnpHttpClient"/>
/// and friends. Bound via <c>services.Configure&lt;HttpTimeoutOptions&gt;(...)</c> (Pattern 7);
/// resolved via <see cref="Microsoft.Extensions.Options.IOptions{T}"/> at consumer ctors.
/// </summary>
public sealed class HttpTimeoutOptions
{
    // --- IUpnpHttpClient per-request budgets (Decision 3) ---
    public TimeSpan DescriptionFetch     { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan ScpdFetch            { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan SoapInvoke           { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan GenaSubscribe        { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan GenaUnsubscribe      { get; init; } = TimeSpan.FromSeconds(5);

    // --- SocketsHttpHandler (shared HttpClient) ---
    public TimeSpan ConnectTimeout       { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan KeepAlivePingDelay   { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan KeepAlivePingTimeout { get; init; } = TimeSpan.FromSeconds(5);

    // --- Inbound GENA callback host (Decision 4 — consumed by Story 4.1) ---
    public TimeSpan CallbackHeaders      { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan CallbackBody         { get; init; } = TimeSpan.FromSeconds(5);

    // --- Per-method response-body size caps (bytes) ---
    // From D3 lines 349-356. The architecture text says these "should live in HttpTimeoutOptions";
    // this story places them here so a single Configure<> call tunes timeouts AND caps.
    public int MaxDescriptionBytes       { get; init; } = 1_048_576;   // 1 MB
    public int MaxScpdBytes              { get; init; } = 2_097_152;   // 2 MB
    public int MaxSoapResponseBytes      { get; init; } = 1_048_576;   // 1 MB
    public int MaxGenaResponseBytes      { get; init; } = 65_536;      // 64 KB
}
