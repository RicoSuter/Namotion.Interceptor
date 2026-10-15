using System.Globalization;
using System.Net;
using System.Text.Json;

namespace HomeBlaze.Services.Diagnostics;

/// <summary>
/// The newest published release, with the version without its <c>v</c> prefix.
/// </summary>
internal sealed record LatestRelease(string Version, DateTimeOffset? PublishedAt, Uri? Url);

/// <summary>
/// A GitHub answer other than success, with the time GitHub asks to wait before the next request.
/// </summary>
internal sealed class GitHubRequestException(string message, TimeSpan? retryAfter) : Exception(message)
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>
/// Reads the latest release of one GitHub repository from the unauthenticated GitHub REST API.
/// </summary>
internal sealed class GitHubReleaseClient
{
    private static readonly Uri ApiBaseAddress = new("https://api.github.com/");
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(30);
    private static readonly long MaximumUnixTimeSeconds = DateTimeOffset.MaxValue.ToUnixTimeSeconds();

    private readonly Uri _latestReleaseUri;
    private readonly Func<HttpClient> _createHttpClient;
    private readonly string _userAgent;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _requestTimeout;

    private GitHubReleaseClient(Uri latestReleaseUri, Func<HttpClient> createHttpClient, string userAgent, TimeProvider timeProvider, TimeSpan requestTimeout)
    {
        _latestReleaseUri = latestReleaseUri;
        _createHttpClient = createHttpClient;
        _userAgent = userAgent;
        _timeProvider = timeProvider;
        _requestTimeout = requestTimeout;
    }

    /// <summary>
    /// Creates a client for a <c>https://github.com/owner/name</c> repository, or returns null for any other URL. A
    /// request is canceled after <paramref name="requestTimeout"/>, 30 seconds by default.
    /// </summary>
    public static GitHubReleaseClient? TryCreate(
        string? repositoryUrl,
        Func<HttpClient> createHttpClient,
        string userAgent,
        TimeProvider timeProvider,
        TimeSpan? requestTimeout = null) =>
        GetLatestReleaseUri(repositoryUrl, out _) is { } latestReleaseUri
            ? new GitHubReleaseClient(latestReleaseUri, createHttpClient, userAgent, timeProvider, requestTimeout ?? DefaultRequestTimeout)
            : null;

    /// <summary>
    /// Returns why no client can be created for <paramref name="repositoryUrl"/>, or null when
    /// <see cref="TryCreate"/> creates one.
    /// </summary>
    public static string? GetUnsupportedReason(string? repositoryUrl)
    {
        GetLatestReleaseUri(repositoryUrl, out var unsupportedReason);
        return unsupportedReason;
    }

    /// <summary>
    /// Returns the latest published release, excluding drafts and pre-releases, or null when there is none.
    /// </summary>
    public async Task<LatestRelease?> GetLatestReleaseAsync(CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_requestTimeout);
        var requestToken = timeoutCts.Token;

        using var request = new HttpRequestMessage(HttpMethod.Get, _latestReleaseUri);
        request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
        request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        using var response = await _createHttpClient().SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw CreateRequestException(response);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(requestToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: requestToken);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("The GitHub release answer is not a JSON object.");
        }

        return new LatestRelease(
            NormalizeVersion(GetRequiredString(root, "tag_name")),
            root.TryGetProperty("published_at", out var publishedAt) &&
            publishedAt.ValueKind == JsonValueKind.String &&
            publishedAt.TryGetDateTimeOffset(out var date) ? date : null,
            GetUri(root, "html_url"));
    }

    private static Uri? GetLatestReleaseUri(string? repositoryUrl, out string? unsupportedReason)
    {
        if (!Uri.TryCreate(repositoryUrl, UriKind.Absolute, out var uri))
        {
            unsupportedReason = "Not available without a repository URL";
            return null;
        }

        if (!string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            unsupportedReason = $"Not supported for {uri.Host}";
            return null;
        }

        if (uri.AbsolutePath.Trim('/').Split('/') is not [{ Length: > 0 } owner, { Length: > 0 } name])
        {
            unsupportedReason = "Not available for this repository URL";
            return null;
        }

        unsupportedReason = null;
        return new Uri(ApiBaseAddress, $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/releases/latest");
    }

    private GitHubRequestException CreateRequestException(HttpResponseMessage response)
    {
        var isRemainingZero = response.Headers.TryGetValues("x-ratelimit-remaining", out var remaining) && remaining.FirstOrDefault() == "0";
        var now = _timeProvider.GetUtcNow();
        TimeSpan? retryAfter = response.Headers.RetryAfter switch
        {
            { Delta: { } delta } => delta,
            { Date: { } date } => date - now,
            _ => null
        };

        if (isRemainingZero &&
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

        // A rate limit answers 403 or 429, which alone does not tell a person why the check failed.
        var isRateLimited = isRemainingZero || (response.Headers.RetryAfter is not null &&
            response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests);
        var message =
            isRateLimited ? "GitHub rate limit reached." :
            response.ReasonPhrase is { Length: > 0 } reason ? $"GitHub answered {(int)response.StatusCode} {reason}." :
            $"GitHub answered {(int)response.StatusCode}.";

        return new GitHubRequestException(message, retryAfter > TimeSpan.Zero ? retryAfter : null);
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
