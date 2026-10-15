using System.Net;
using System.Text;

namespace HomeBlaze.Services.Tests.Diagnostics;

/// <summary>
/// Answers requests with the given function and keeps the requests it received.
/// </summary>
internal sealed class FakeHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : this((request, _) => Task.FromResult(respond(request)))
    {
    }

    /// <summary>
    /// A latest release answer for version 0.6.0 of <c>owner/repo</c>.
    /// </summary>
    public const string ReleaseJson = """
        { "tag_name": "v0.6.0", "published_at": "2026-10-20T08:00:00Z", "html_url": "https://github.com/owner/repo/releases/tag/v0.6.0" }
        """;

    public List<HttpRequestMessage> Requests { get; } = [];

    public static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return respond(request, cancellationToken);
    }
}
