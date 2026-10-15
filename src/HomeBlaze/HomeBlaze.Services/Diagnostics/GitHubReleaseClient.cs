using System.Globalization;
using System.Net;
using System.Text.Json;

namespace HomeBlaze.Services.Diagnostics;

/// <summary>
/// A GitHub answer other than success, with the time GitHub asks to wait before the next request.
/// </summary>
internal sealed class GitHubRequestException(string message, HttpStatusCode statusCode, TimeSpan? retryAfter) : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;

    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>
/// Reads the latest release and branch comparisons from the unauthenticated GitHub REST API. Requests repeat the last
/// ETag, and GitHub does not count a <c>304 Not Modified</c> answer against the rate limit. Not thread-safe; the update
/// loop is its only caller.
/// </summary>
internal sealed class GitHubReleaseClient
{
    private static readonly Uri ApiBaseAddress = new("https://api.github.com/");

    private readonly Func<HttpClient> _createHttpClient;
    private readonly string _userAgent;
    private readonly TimeProvider _timeProvider;

    private CachedResponse<LatestRelease>? _release;
    private CachedResponse<BranchComparison>? _comparison;

    private sealed record CachedResponse<T>(Uri RequestUri, string ETag, T Value);

    public GitHubReleaseClient(Func<HttpClient> createHttpClient, string userAgent, TimeProvider timeProvider)
    {
        _createHttpClient = createHttpClient;
        _userAgent = userAgent;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Returns the owner and name of a <c>https://github.com/owner/name</c> URL, or null for any other URL.
    /// </summary>
    public static (string Owner, string Name)? TryParseRepository(string? repositoryUrl)
    {
        if (!Uri.TryCreate(repositoryUrl, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var segments = uri.AbsolutePath.Trim('/').Split('/');
        return segments is [{ Length: > 0 } owner, { Length: > 0 } name] ? (owner, name) : null;
    }

    /// <summary>
    /// Returns the latest published release, excluding drafts and pre-releases, or null when there is none.
    /// </summary>
    public async Task<LatestRelease?> GetLatestReleaseAsync(string owner, string name, CancellationToken cancellationToken)
    {
        var requestUri = new Uri(ApiBaseAddress, $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/releases/latest");
        var cached = _release is { } release && release.RequestUri == requestUri ? release : null;

        using var response = await SendAsync(requestUri, cached?.ETag, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotModified && cached is not null)
        {
            return cached.Value;
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            _release = null;
            return null;
        }

        using var document = await ReadJsonAsync(response, cancellationToken);
        var root = document.RootElement;
        var value = new LatestRelease(
            GetRequiredString(root, "tag_name").TrimStart('v', 'V'),
            root.TryGetProperty("published_at", out var publishedAt) &&
            publishedAt.ValueKind == JsonValueKind.String &&
            publishedAt.TryGetDateTimeOffset(out var date) ? date : null,
            GetUri(root, "html_url"));

        _release = response.Headers.ETag?.ToString() is { } etag ? new CachedResponse<LatestRelease>(requestUri, etag, value) : null;
        return value;
    }

    /// <summary>
    /// Returns how many commits <paramref name="branch"/> has that <paramref name="commit"/> does not.
    /// </summary>
    public async Task<BranchComparison> CompareWithBranchAsync(string owner, string name, string commit, string branch, CancellationToken cancellationToken)
    {
        // per_page=1 keeps the commit list short; ahead_by still counts all commits.
        var requestUri = new Uri(ApiBaseAddress,
            $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/compare/{Uri.EscapeDataString(commit)}...{Uri.EscapeDataString(branch)}?per_page=1");
        var cached = _comparison is { } comparison && comparison.RequestUri == requestUri ? comparison : null;

        using var response = await SendAsync(requestUri, cached?.ETag, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotModified && cached is not null)
        {
            return cached.Value;
        }

        using var document = await ReadJsonAsync(response, cancellationToken);
        var root = document.RootElement;
        if (!root.TryGetProperty("ahead_by", out var aheadBy) || !aheadBy.TryGetInt32(out var commitsBehind))
        {
            throw new JsonException("The GitHub comparison has no ahead_by count.");
        }

        var value = new BranchComparison(commitsBehind, GetUri(root, "html_url"));
        _comparison = response.Headers.ETag?.ToString() is { } etag ? new CachedResponse<BranchComparison>(requestUri, etag, value) : null;
        return value;
    }

    private async Task<HttpResponseMessage> SendAsync(Uri requestUri, string? etag, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
        request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        if (etag is not null)
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        }

        return await _createHttpClient().SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new GitHubRequestException(
                $"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}.", response.StatusCode, GetRetryAfter(response));
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private TimeSpan? GetRetryAfter(HttpResponseMessage response)
    {
        var now = _timeProvider.GetUtcNow();
        TimeSpan? retryAfter = response.Headers.RetryAfter switch
        {
            { Delta: { } delta } => delta,
            { Date: { } date } => date - now,
            _ => null
        };

        if (response.Headers.TryGetValues("x-ratelimit-remaining", out var remaining) && remaining.FirstOrDefault() == "0" &&
            response.Headers.TryGetValues("x-ratelimit-reset", out var reset) &&
            long.TryParse(reset.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var resetSeconds))
        {
            var untilReset = DateTimeOffset.FromUnixTimeSeconds(resetSeconds) - now;
            if (retryAfter is null || untilReset > retryAfter)
            {
                retryAfter = untilReset;
            }
        }

        return retryAfter > TimeSpan.Zero ? retryAfter : null;
    }

    private static string GetRequiredString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String &&
        property.GetString() is { Length: > 0 } value
            ? value
            : throw new JsonException($"The GitHub answer has no {propertyName}.");

    private static Uri? GetUri(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String &&
        Uri.TryCreate(property.GetString(), UriKind.Absolute, out var uri)
            ? uri
            : null;
}
