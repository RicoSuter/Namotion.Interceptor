using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Xunit;
using static HomeBlaze.Installation.Tests.FakeHttpMessageHandler;

namespace HomeBlaze.Installation.Tests;

public class GitHubRepositoryClientTests
{
    private const string RateLimitMessage = "GitHub rate limit reached.";

    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private static GitHubRepositoryClient CreateClient(FakeHttpMessageHandler handler, string repositoryUrl = "https://github.com/owner/repo") =>
        GitHubRepositoryClient.TryCreate(repositoryUrl, () => new HttpClient(handler), "HomeBlaze/0.5.0", new TestTimeProvider(Now))!;

    [Theory]
    [InlineData("https://github.com/RicoSuter/Namotion.Interceptor", "https://api.github.com/repos/RicoSuter/Namotion.Interceptor/releases/latest")]
    [InlineData("https://GitHub.com/owner/repo", "https://api.github.com/repos/owner/repo/releases/latest")]
    public async Task WhenTheLatestReleaseIsRead_ThenItIsParsedAndTheRequestIsPolite(string repositoryUrl, string expectedRequestUri)
    {
        // Arrange
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.Json(ReleaseJson));
        var client = CreateClient(handler, repositoryUrl);

        // Act
        var release = await client.GetLatestReleaseAsync(CancellationToken.None);

        // Assert
        Assert.Equal(new LatestRelease("0.6.0", new DateTimeOffset(2026, 10, 20, 8, 0, 0, TimeSpan.Zero), new Uri("https://github.com/owner/repo/releases/tag/v0.6.0")), release);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(expectedRequestUri, request.RequestUri!.ToString());
        Assert.Equal("HomeBlaze/0.5.0", string.Join(' ', request.Headers.GetValues("User-Agent")));
    }

    [Theory]
    [InlineData("https://gitlab.com/owner/repo", "Not supported for gitlab.com")]
    [InlineData("https://github.com/owner", "Not available for this repository URL")]
    [InlineData("not a URL", "Not available without a repository URL")]
    [InlineData(null, "Not available without a repository URL")]
    public void WhenTheUrlIsNotAGitHubRepository_ThenNoClientIsCreatedAndTheReasonIsReported(string? repositoryUrl, string expectedReason)
    {
        // Act
        var client = GitHubRepositoryClient.TryCreate(repositoryUrl, () => throw new InvalidOperationException(), "HomeBlaze/0.5.0", TimeProvider.System);
        var reason = GitHubRepositoryClient.GetUnsupportedReason(repositoryUrl);

        // Assert
        Assert.Null(client);
        Assert.Equal(expectedReason, reason);
    }

    [Fact]
    public async Task WhenTheRepositoryHasNoRelease_ThenNullIsReturned()
    {
        // Arrange
        var client = CreateClient(new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));

        // Act
        var release = await client.GetLatestReleaseAsync(CancellationToken.None);

        // Assert
        Assert.Null(release);
    }

    [Theory]
    [InlineData("RateLimitWithReset", RateLimitMessage, 90.0)]
    [InlineData("RateLimitWithoutReset", RateLimitMessage, null)]
    [InlineData("RateLimitWithResetOutOfRange", RateLimitMessage, null)]
    [InlineData("TooManyRequestsWithRetryAfterDelay", RateLimitMessage, 5.0)]
    [InlineData("TooManyRequestsWithRetryAfterDate", RateLimitMessage, 10.0)]
    [InlineData("ServerError", "GitHub answered 500 Internal Server Error.", null)]
    [InlineData("UnknownStatusWithoutReason", "GitHub answered 599.", null)]
    public async Task WhenGitHubAnswersWithAnError_ThenTheExceptionDescribesItAndCarriesTheRequestedWait(
        string answer, string expectedMessage, double? expectedRetryAfterMinutes)
    {
        // Arrange
        var client = CreateClient(new FakeHttpMessageHandler(_ => CreateErrorAnswer(answer)));

        // Act & Assert
        var exception = await Assert.ThrowsAsync<GitHubRequestException>(() => client.GetLatestReleaseAsync(CancellationToken.None));
        Assert.Equal(expectedMessage, exception.Message);
        Assert.Equal(expectedRetryAfterMinutes is { } minutes ? TimeSpan.FromMinutes(minutes) : null, exception.RetryAfter);
    }

    [Theory]
    [InlineData("""{ "name": "x" }""")]
    [InlineData("[]")]
    public async Task WhenTheReleaseAnswerHasNoTagOrIsNotAnObject_ThenAJsonExceptionIsThrown(string json)
    {
        // Arrange
        var client = CreateClient(new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.Json(json)));

        // Act & Assert
        await Assert.ThrowsAsync<JsonException>(() => client.GetLatestReleaseAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("v0.6.0", "0.6.0")]
    [InlineData("V1.2.3", "1.2.3")]
    [InlineData("version-1", "version-1")]
    [InlineData("v-next", "v-next")]
    public async Task WhenTheReleaseTagHasAVersionPrefix_ThenOnlyALeadingVFollowedByADigitIsStripped(string tagName, string expectedVersion)
    {
        // Arrange
        var json = $$"""{ "tag_name": "{{tagName}}" }""";
        var client = CreateClient(new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.Json(json)));

        // Act
        var release = await client.GetLatestReleaseAsync(CancellationToken.None);

        // Assert
        Assert.Equal(expectedVersion, release!.Version);
    }

    // The commit is the base, so ahead_by counts the commits the commit lacks and behind_by those the branch lacks.
    [Theory]
    [InlineData(2, 0)]
    [InlineData(0, 0)]
    [InlineData(3, 1)]
    [InlineData(0, 4)]
    public async Task WhenACommitIsComparedWithABranch_ThenAheadByIsTheCommitsBehindAndBehindByTheCommitsAhead(int aheadBy, int behindBy)
    {
        // Arrange
        var client = CreateClient(new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.Json(ComparisonJson(aheadBy, behindBy))));

        // Act
        var comparison = await client.CompareWithBranchAsync("b1b7c2f", "master", CancellationToken.None);

        // Assert
        Assert.Equal(new BranchComparison(CommitsBehind: aheadBy, CommitsAhead: behindBy, new Uri(CompareUrl)), comparison);
    }

    [Theory]
    [InlineData("master", "https://api.github.com/repos/owner/repo/compare/b1b7c2f...master?per_page=1&page=2")]
    [InlineData("feature/name", "https://api.github.com/repos/owner/repo/compare/b1b7c2f...feature/name?per_page=1&page=2")]
    [InlineData("a b#c", "https://api.github.com/repos/owner/repo/compare/b1b7c2f...a%20b%23c?per_page=1&page=2")]
    public async Task WhenACommitIsComparedWithABranch_ThenThePageWithoutFilesIsRequestedWithoutAuthorization(string branch, string expectedRequestUri)
    {
        // Arrange
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.Json(ComparisonJson(1, 0)));
        var client = CreateClient(handler);

        // Act
        await client.CompareWithBranchAsync("b1b7c2f", branch, CancellationToken.None);

        // Assert
        var request = Assert.Single(handler.Requests);
        Assert.Equal(expectedRequestUri, request.RequestUri!.AbsoluteUri);
        Assert.Equal("HomeBlaze/0.5.0", string.Join(' ', request.Headers.GetValues("User-Agent")));
        Assert.False(request.Headers.Contains("Authorization"));
    }

    [Fact]
    public async Task WhenGitHubDoesNotKnowTheCommitOrBranch_ThenNullIsReturned()
    {
        // Arrange
        var client = CreateClient(new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));

        // Act
        var comparison = await client.CompareWithBranchAsync("b1b7c2f", "master", CancellationToken.None);

        // Assert
        Assert.Null(comparison);
    }

    [Fact]
    public async Task WhenTheComparisonIsRateLimited_ThenTheExceptionCarriesTheRequestedWait()
    {
        // Arrange
        var client = CreateClient(new FakeHttpMessageHandler(_ => CreateErrorAnswer("RateLimitWithReset")));

        // Act & Assert
        var exception = await Assert.ThrowsAsync<GitHubRequestException>(() => client.CompareWithBranchAsync("b1b7c2f", "master", CancellationToken.None));
        Assert.Equal(RateLimitMessage, exception.Message);
        Assert.Equal(TimeSpan.FromMinutes(90), exception.RetryAfter);
    }

    [Theory]
    [InlineData("""{ "behind_by": 0 }""")]
    [InlineData("""{ "ahead_by": "2", "behind_by": 0 }""")]
    [InlineData("""{ "ahead_by": -1, "behind_by": 0 }""")]
    [InlineData("[]")]
    public async Task WhenTheComparisonAnswerHasNoValidCountsOrIsNotAnObject_ThenAJsonExceptionIsThrown(string json)
    {
        // Arrange
        var client = CreateClient(new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.Json(json)));

        // Act & Assert
        await Assert.ThrowsAsync<JsonException>(() => client.CompareWithBranchAsync("b1b7c2f", "master", CancellationToken.None));
    }

    [Fact]
    public async Task WhenTheCallerTokenIsAlreadyCanceled_ThenOperationCanceledExceptionIsThrown()
    {
        // Arrange
        using var cancellationTokenSource = new CancellationTokenSource();
        await cancellationTokenSource.CancelAsync();
        var client = CreateClient(new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.Json(ReleaseJson)));

        // Act & Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetLatestReleaseAsync(cancellationTokenSource.Token));
    }

    private static HttpResponseMessage CreateErrorAnswer(string answer) => answer switch
    {
        "RateLimitWithReset" => CreateRateLimitAnswer(Now.AddMinutes(90).ToUnixTimeSeconds().ToString()),
        "RateLimitWithoutReset" => CreateRateLimitAnswer(reset: null),
        "RateLimitWithResetOutOfRange" => CreateRateLimitAnswer("99999999999999"),
        "TooManyRequestsWithRetryAfterDelay" => new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Headers = { RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(5)) } },
        "TooManyRequestsWithRetryAfterDate" => new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Headers = { RetryAfter = new RetryConditionHeaderValue(Now.AddMinutes(10)) } },
        "ServerError" => new HttpResponseMessage(HttpStatusCode.InternalServerError),
        "UnknownStatusWithoutReason" => new HttpResponseMessage((HttpStatusCode)599),
        _ => throw new ArgumentOutOfRangeException(nameof(answer), answer, null)
    };

    private static HttpResponseMessage CreateRateLimitAnswer(string? reset)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
        response.Headers.Add("x-ratelimit-remaining", "0");
        if (reset is not null)
        {
            response.Headers.Add("x-ratelimit-reset", reset);
        }

        return response;
    }
}
