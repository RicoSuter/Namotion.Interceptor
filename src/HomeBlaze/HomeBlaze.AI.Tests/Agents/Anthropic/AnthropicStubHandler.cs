using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace HomeBlaze.AI.Tests.Agents.Anthropic;

/// <summary>A request the Anthropic client sent, with its API key header and body.</summary>
internal sealed record CapturedRequest(string? ApiKeyHeader, bool HasAuthorization, string Body);

/// <summary>Answers every Anthropic API request with the same response and records the requests.</summary>
internal sealed class AnthropicStubHandler(HttpStatusCode statusCode, string body, string mediaType = "application/json") : HttpMessageHandler
{
    public List<CapturedRequest> Requests { get; } = [];

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
            request.Headers.TryGetValues("x-api-key", out var values) ? values.Single() : null,
            request.Headers.Authorization is not null,
            await request.Content.ReadAsStringAsync(cancellationToken)));

        var response = new HttpResponseMessage(statusCode) { Content = new StringContent(body, Encoding.UTF8, mediaType) };
        // Keeps the SDK from retrying, which would wait between attempts.
        response.Headers.Add("x-should-retry", "false");
        return response;
    }
}
