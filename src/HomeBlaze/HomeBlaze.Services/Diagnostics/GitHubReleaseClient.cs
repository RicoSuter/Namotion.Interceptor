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
/// Reads the latest release and branch comparisons from the unauthenticated GitHub REST API. Requests repeat the
/// last ETag so an unchanged answer is not downloaded and parsed again. Not thread-safe; the update loop is its
/// only caller.
/// </summary>
internal sealed class GitHubReleaseClient
{
    private static readonly Uri ApiBaseAddress = new("https://api.github.com/");
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    private static readonly long MaximumUnixTimeSeconds = DateTimeOffset.MaxValue.ToUnixTimeSeconds();

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
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(RequestTimeout);
        var requestToken = timeoutCts.Token;

        var requestUri = new Uri(ApiBaseAddress, $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/releases/latest");
        var cached = _release is { } release && release.RequestUri == requestUri ? release : null;

        using var response = await SendAsync(requestUri, cached?.ETag, requestToken);
        if (response.StatusCode == HttpStatusCode.NotModified && cached is not null)
        {
            return cached.Value;
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            _release = null;
            return null;
        }

        using var document = await ReadJsonAsync(response, requestToken);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("The GitHub release answer is not a JSON object.");
        }

        var value = new LatestRelease(
            NormalizeVersion(GetRequiredString(root, "tag_name")),
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
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(RequestTimeout);
        var requestToken = timeoutCts.Token;

        // The files list is only on the first page; page 2 keeps the answer small and still has ahead_by.
        var requestUri = new Uri(ApiBaseAddress,
            $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/compare/{Uri.EscapeDataString(commit)}...{Uri.EscapeDataString(branch)}?page=2&per_page=1");
        var cached = _comparison is { } comparison && comparison.RequestUri == requestUri ? comparison : null;

        using var response = await SendAsync(requestUri, cached?.ETag, requestToken);
        if (response.StatusCode == HttpStatusCode.NotModified && cached is not null)
        {
            return cached.Value;
        }

        using var document = await ReadJsonAsync(response, requestToken);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("The GitHub comparison answer is not a JSON object.");
        }

        if (!root.TryGetProperty("ahead_by", out var aheadBy) || aheadBy.ValueKind != JsonValueKind.Number || !aheadBy.TryGetInt32(out var commitsBehind))
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
            throw new GitHubRequestException(BuildErrorMessage(response), response.StatusCode, GetRetryAfter(response));
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Describes a failed answer for display.
    /// </summary>
    private static string BuildErrorMessage(HttpResponseMessage response)
    {
        // A rate limit answers 403 or 429, which alone does not tell a person why the check failed.
        if (IsRateLimited(response))
        {
            return "GitHub rate limit reached.";
        }

        return response.ReasonPhrase is { Length: > 0 } reason
            ? $"GitHub answered {(int)response.StatusCode} {reason}."
            : $"GitHub answered {(int)response.StatusCode}.";
    }

    private static bool IsRateLimited(HttpResponseMessage response)
    {
        var remainingIsZero = response.Headers.TryGetValues("x-ratelimit-remaining", out var remaining) && remaining.FirstOrDefault() == "0";
        var retryAfterOnLimitStatus = response.Headers.RetryAfter is not null &&
            (response.StatusCode == HttpStatusCode.Forbidden || response.StatusCode == HttpStatusCode.TooManyRequests);

        return remainingIsZero || retryAfterOnLimitStatus;
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
            long.TryParse(reset.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var resetSeconds) &&
            resetSeconds <= MaximumUnixTimeSeconds)
        {
            var untilReset = DateTimeOffset.FromUnixTimeSeconds(resetSeconds) - now;
            if (retryAfter is null || untilReset > retryAfter)
            {
                retryAfter = untilReset;
            }
        }

        return retryAfter > TimeSpan.Zero ? retryAfter : null;
    }

    /// <summary>
    /// Strips a single leading <c>v</c> or <c>V</c> from a tag name, but only when it is immediately followed by a
    /// digit, so a tag such as <c>version-1</c> is left untouched.
    /// </summary>
    private static string NormalizeVersion(string tagName) =>
        tagName.Length > 1 && (tagName[0] == 'v' || tagName[0] == 'V') && char.IsAsciiDigit(tagName[1])
            ? tagName[1..]
            : tagName;

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
