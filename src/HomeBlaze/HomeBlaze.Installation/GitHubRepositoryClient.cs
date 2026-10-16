using System.Globalization;
using System.Net;
using System.Text.Json;

namespace HomeBlaze.Installation;

/// <summary>
/// The newest published release, with the version without its <c>v</c> prefix.
/// </summary>
internal sealed record LatestRelease(string Version, DateTimeOffset? PublishedAt, Uri? Url);

/// <summary>
/// How a commit relates to the head of a branch: the commits of the branch the commit lacks, the commits of the commit
/// the branch lacks, and the GitHub page that lists the commits the commit lacks.
/// </summary>
internal sealed record BranchComparison(int CommitsBehind, int CommitsAhead, Uri? Url);

/// <summary>
/// A GitHub answer other than success, with the time GitHub asks to wait before the next request.
/// </summary>
public sealed class GitHubRequestException(string message, TimeSpan? retryAfter) : Exception(message)
{
    /// <summary>
    /// The time GitHub asked to wait before the next request, or null when it gave none or no wait remains.
    /// </summary>
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>
/// Reads the latest release of one GitHub repository, and compares commits with its branches, from the unauthenticated
/// GitHub REST API.
/// </summary>
internal sealed class GitHubRepositoryClient
{
    private static readonly Uri ApiBaseAddress = new("https://api.github.com/");
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(30);
    private static readonly long MaximumUnixTimeSeconds = DateTimeOffset.MaxValue.ToUnixTimeSeconds();

    // Ends with a slash, so request paths resolve below it.
    private readonly Uri _repositoryApiUri;
    private readonly Func<HttpClient> _createHttpClient;
    private readonly string _userAgent;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _requestTimeout;

    private GitHubRepositoryClient(Uri repositoryApiUri, Func<HttpClient> createHttpClient, string userAgent, TimeProvider timeProvider, TimeSpan requestTimeout)
    {
        _repositoryApiUri = repositoryApiUri;
        _createHttpClient = createHttpClient;
        _userAgent = userAgent;
        _timeProvider = timeProvider;
        _requestTimeout = requestTimeout;
    }

    /// <summary>
    /// Creates a client for a <c>https://github.com/owner/name</c> repository, or returns null for any other URL. A
    /// request is canceled after <paramref name="requestTimeout"/>, 30 seconds by default.
    /// </summary>
    public static GitHubRepositoryClient? TryCreate(
        string? repositoryUrl,
        Func<HttpClient> createHttpClient,
        string userAgent,
        TimeProvider timeProvider,
        TimeSpan? requestTimeout = null) =>
        GetRepositoryApiUri(repositoryUrl, out _) is { } repositoryApiUri
            ? new GitHubRepositoryClient(repositoryApiUri, createHttpClient, userAgent, timeProvider, requestTimeout ?? DefaultRequestTimeout)
            : null;

    /// <summary>
    /// Returns why no client can be created for <paramref name="repositoryUrl"/>, or null when
    /// <see cref="TryCreate"/> creates one.
    /// </summary>
    public static string? GetUnsupportedReason(string? repositoryUrl)
    {
        GetRepositoryApiUri(repositoryUrl, out var unsupportedReason);
        return unsupportedReason;
    }

    /// <summary>
    /// Returns the latest published release, excluding drafts and pre-releases, or null when there is none.
    /// </summary>
    public async Task<LatestRelease?> GetLatestReleaseAsync(CancellationToken cancellationToken)
    {
        using var document = await GetAsync("releases/latest", cancellationToken);
        if (document is null)
        {
            return null;
        }

        var root = GetObject(document, "release");
        return new LatestRelease(
            NormalizeVersion(GetRequiredString(root, "tag_name")),
            root.TryGetProperty("published_at", out var publishedAt) &&
            publishedAt.ValueKind == JsonValueKind.String &&
            publishedAt.TryGetDateTimeOffset(out var date) ? date : null,
            GetUri(root, "html_url"));
    }

    /// <summary>
    /// Compares <paramref name="commit"/> with the head of <paramref name="branch"/>, or returns null when GitHub cannot
    /// compare them, such as when it does not know the commit or the branch.
    /// </summary>
    public async Task<BranchComparison?> CompareWithBranchAsync(string commit, string branch, CancellationToken cancellationToken)
    {
        // Only the first page carries the changed files with their patches; the second one still carries the counts, also
        // when it holds no commit. The commit is the base, so ahead_by counts the commits of the branch it lacks.
        var path = $"compare/{Uri.EscapeDataString(commit)}...{EscapePath(branch)}?per_page=1&page=2";
        using var document = await GetAsync(path, cancellationToken);
        if (document is null)
        {
            return null;
        }

        var root = GetObject(document, "comparison");
        return new BranchComparison(
            GetRequiredCount(root, "ahead_by"),
            GetRequiredCount(root, "behind_by"),
            GetUri(root, "html_url"));
    }

    /// <summary>
    /// Sends a GET request for <paramref name="path"/> below the repository and returns the parsed answer, or null
    /// when GitHub answers 404.
    /// </summary>
    private async Task<JsonDocument?> GetAsync(string path, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_requestTimeout);
        var requestToken = timeoutCts.Token;

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_repositoryApiUri, path));
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
        return await JsonDocument.ParseAsync(stream, cancellationToken: requestToken);
    }

    private static Uri? GetRepositoryApiUri(string? repositoryUrl, out string? unsupportedReason)
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
        return new Uri(ApiBaseAddress, $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/");
    }

    private GitHubRequestException CreateRequestException(HttpResponseMessage response)
    {
        var isRemainingZero = response.Headers.TryGetValues("x-ratelimit-remaining", out var remaining) && remaining.FirstOrDefault() == "0";
        var retryAfter = GetRetryAfter(response, isRemainingZero, _timeProvider.GetUtcNow());
        return new GitHubRequestException(GetFailureMessage(response, isRemainingZero), retryAfter > TimeSpan.Zero ? retryAfter : null);
    }

    /// <summary>
    /// Returns the wait of the <c>Retry-After</c> header or, when no requests remain, the longer wait until the rate
    /// limit resets.
    /// </summary>
    private static TimeSpan? GetRetryAfter(HttpResponseMessage response, bool isRemainingZero, DateTimeOffset now)
    {
        TimeSpan? retryAfter = response.Headers.RetryAfter switch
        {
            { Delta: { } delta } => delta,
            { Date: { } date } => date - now,
            _ => null
        };

        if (isRemainingZero && TryGetRateLimitReset(response, out var reset))
        {
            var untilReset = reset - now;
            if (retryAfter is null || untilReset > retryAfter)
            {
                retryAfter = untilReset;
            }
        }

        return retryAfter;
    }

    private static bool TryGetRateLimitReset(HttpResponseMessage response, out DateTimeOffset reset)
    {
        if (response.Headers.TryGetValues("x-ratelimit-reset", out var values) &&
            long.TryParse(values.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var resetSeconds) &&
            resetSeconds <= MaximumUnixTimeSeconds)
        {
            reset = DateTimeOffset.FromUnixTimeSeconds(resetSeconds);
            return true;
        }

        reset = default;
        return false;
    }

    private static string GetFailureMessage(HttpResponseMessage response, bool isRemainingZero)
    {
        // A rate limit answers 403 or 429, which alone does not tell a person why the check failed.
        var isRateLimited = isRemainingZero || (response.Headers.RetryAfter is not null &&
            response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests);
        if (isRateLimited)
        {
            return "GitHub rate limit reached.";
        }

        return response.ReasonPhrase is { Length: > 0 } reason
            ? $"GitHub answered {(int)response.StatusCode} {reason}."
            : $"GitHub answered {(int)response.StatusCode}.";
    }

    /// <summary>
    /// Strips a single leading <c>v</c> or <c>V</c> from a tag name, but only when it is immediately followed by a
    /// digit, so a tag such as <c>version-1</c> is left untouched.
    /// </summary>
    private static string NormalizeVersion(string tagName) =>
        tagName is ['v' or 'V', var second, ..] && char.IsAsciiDigit(second)
            ? tagName[1..]
            : tagName;

    /// <summary>
    /// Escapes each segment of a branch name such as <c>feature/name</c> and keeps the slashes between them.
    /// </summary>
    private static string EscapePath(string value) =>
        string.Join('/', value.Split('/').Select(Uri.EscapeDataString));

    private static JsonElement GetObject(JsonDocument document, string answerName) =>
        document.RootElement.ValueKind == JsonValueKind.Object
            ? document.RootElement
            : throw new JsonException($"The GitHub {answerName} answer is not a JSON object.");

    private static int GetRequiredCount(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Number &&
            property.TryGetInt32(out var value))
        {
            return value >= 0 ? value : throw new JsonException($"The GitHub answer has a negative {propertyName}.");
        }

        throw new JsonException($"The GitHub answer has no {propertyName}.");
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
