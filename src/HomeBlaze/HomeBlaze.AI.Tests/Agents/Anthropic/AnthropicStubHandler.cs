using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace HomeBlaze.AI.Tests.Agents.Anthropic;

/// <summary>A request the Anthropic client sent, with its URL, API key header and body.</summary>
internal sealed record CapturedRequest(Uri? RequestUri, string? ApiKeyHeader, bool HasAuthorization, string Body);

/// <summary>
/// Answers each Anthropic API request with the next of its responses, the last one again once all were sent, and
/// records the requests.
/// </summary>
internal sealed class AnthropicStubHandler : HttpMessageHandler
{
    private readonly (HttpStatusCode StatusCode, string Body, string MediaType)[] _responses;

    /// <summary>Answers every request with the same response.</summary>
    public AnthropicStubHandler(HttpStatusCode statusCode, string body, string mediaType = "application/json")
        : this([(statusCode, body, mediaType)])
    {
    }

    private AnthropicStubHandler((HttpStatusCode StatusCode, string Body, string MediaType)[] responses) => _responses = responses;

    public List<CapturedRequest> Requests { get; } = [];

    /// <summary>Answers each request with the next event stream.</summary>
    public static AnthropicStubHandler EventStreams(params string[] streams) =>
        new(streams.Select(stream => (HttpStatusCode.OK, stream, "text/event-stream")).ToArray());

    /// <summary>Answers each request with the next event stream, and the requests after them with the error.</summary>
    public static AnthropicStubHandler EventStreamsThenError(HttpStatusCode statusCode, string errorBody, params string[] streams) =>
        new([.. streams.Select(stream => (HttpStatusCode.OK, stream, "text/event-stream")), (statusCode, errorBody, "application/json")]);

    /// <summary>The body of an API error response.</summary>
    public static string ErrorBody(string type, string message) =>
        JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["type"] = "error",
            ["error"] = new Dictionary<string, string> { ["type"] = type, ["message"] = message }
        });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Assert.NotNull(request.Content);
        Requests.Add(new CapturedRequest(
            request.RequestUri,
            request.Headers.TryGetValues("x-api-key", out var values) ? values.Single() : null,
            request.Headers.Authorization is not null,
            await request.Content.ReadAsStringAsync(cancellationToken)));

        var (statusCode, body, mediaType) = _responses[Math.Min(Requests.Count, _responses.Length) - 1];
        var response = new HttpResponseMessage(statusCode) { Content = new StringContent(body, Encoding.UTF8, mediaType) };
        // Keeps the SDK from retrying, which would wait between attempts.
        response.Headers.Add("x-should-retry", "false");
        return response;
    }
}
