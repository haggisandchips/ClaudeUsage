using System.Net.Http.Headers;

namespace ClaudeUsage.Tests.TestSupport;

internal sealed record CapturedRequest(HttpMethod Method, Uri Uri, string? Body, HttpRequestHeaders Headers);

/// <summary>Routes requests to a user-supplied responder instead of hitting the network.</summary>
internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<CapturedRequest, HttpResponseMessage> _responder;

    public List<CapturedRequest> Requests { get; } = new();

    public FakeHttpMessageHandler(Func<CapturedRequest, HttpResponseMessage> responder)
    {
        _responder = responder;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var captured = new CapturedRequest(request.Method, request.RequestUri!, body, request.Headers);
        Requests.Add(captured);
        return _responder(captured);
    }
}
