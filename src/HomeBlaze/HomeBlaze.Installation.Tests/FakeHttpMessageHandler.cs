using System.Net;
using System.Text;

namespace HomeBlaze.Installation.Tests;

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

    /// <summary>
    /// The URL of the comparison answer of <see cref="ComparisonJson"/>.
    /// </summary>
    public const string CompareUrl = "https://github.com/owner/repo/compare/b1b7c2f...master";

    public List<HttpRequestMessage> Requests { get; } = [];

    /// <summary>
    /// A comparison answer of a commit with a branch of <c>owner/repo</c>, where <paramref name="aheadBy"/> counts the
    /// commits of the branch that the commit lacks.
    /// </summary>
    public static string ComparisonJson(int aheadBy, int behindBy)
    {
        var status = (aheadBy, behindBy) switch
        {
            (0, 0) => "identical",
            (_, 0) => "ahead",
            (0, _) => "behind",
            _ => "diverged"
        };
        return $$"""{ "html_url": "{{CompareUrl}}", "status": "{{status}}", "ahead_by": {{aheadBy}}, "behind_by": {{behindBy}}, "total_commits": {{aheadBy}}, "commits": [] }""";
    }

    public static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return respond(request, cancellationToken);
    }
}
