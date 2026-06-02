namespace ohSpy.Core.Tests.Fakes;

/// <summary>
/// Hand-rolled <see cref="HttpMessageHandler"/> test fake. Use over Moq's
/// <c>Protected()</c> because (a) compile-time type safety, (b) clearer test
/// assertions, (c) supports both pre-built response and per-request lambda response.
/// </summary>
internal sealed class TestHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responder;

    /// <summary>Captured requests in arrival order. Tests assert against this.</summary>
    public List<HttpRequestMessage> Requests { get; } = new();

    /// <summary>Construct with a per-request responder.</summary>
    public TestHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
    {
        ArgumentNullException.ThrowIfNull(responder);
        _responder = responder;
    }

    /// <summary>Construct returning a fixed response.</summary>
    public TestHttpMessageHandler(HttpResponseMessage fixedResponse)
    {
        ArgumentNullException.ThrowIfNull(fixedResponse);
        _responder = (_, _) => Task.FromResult(fixedResponse);
    }

    /// <summary>Construct returning a fixed status + body.</summary>
    public static TestHttpMessageHandler WithBody(System.Net.HttpStatusCode status, string body, string contentType = "text/xml") =>
        new(new HttpResponseMessage(status)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, contentType),
        });

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return await _responder(request, cancellationToken).ConfigureAwait(false);
    }
}
