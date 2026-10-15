using System.Net;
using System.Text.Json;
using HomeBlaze.Services.Diagnostics;
using Xunit;

namespace HomeBlaze.Services.Tests.Diagnostics;

public class GitHubReleaseClientTests
{
    private const string ReleaseJson = """
        { "tag_name": "v0.6.0", "published_at": "2026-10-20T08:00:00Z", "html_url": "https://github.com/owner/repo/releases/tag/v0.6.0" }
        """;

    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private static GitHubReleaseClient CreateClient(FakeHttpMessageHandler handler) =>
        new(() => new HttpClient(handler), "HomeBlaze/0.5.0", new TestTimeProvider(Now));

    [Theory]
    [InlineData("https://github.com/RicoSuter/Namotion.Interceptor", "RicoSuter", "Namotion.Interceptor")]
    [InlineData("https://GitHub.com/owner/repo", "owner", "repo")]
    public void WhenAGitHubUrlIsParsed_ThenOwnerAndNameAreReturned(string url, string owner, string name)
    {
        // Act
        var repository = GitHubReleaseClient.TryParseRepository(url);

        // Assert
        Assert.Equal((owner, name), repository!.Value);
    }

    [Theory]
    [InlineData("https://gitlab.com/owner/repo")]
    [InlineData("https://github.com/owner")]
    [InlineData(null)]
    public void WhenTheUrlIsNotAGitHubRepository_ThenNullIsReturned(string? url)
    {
        // Act
        var repository = GitHubReleaseClient.TryParseRepository(url);

        // Assert
        Assert.Null(repository);
    }

    [Fact]
    public async Task WhenTheLatestReleaseIsRead_ThenItIsParsedAndTheRequestIsPolite()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.Json(ReleaseJson, "\"abc\""));
        var client = CreateClient(handler);

        // Act
        var release = await client.GetLatestReleaseAsync("owner", "repo", CancellationToken.None);

        // Assert
        Assert.Equal(new LatestRelease("0.6.0", new DateTimeOffset(2026, 10, 20, 8, 0, 0, TimeSpan.Zero), new Uri("https://github.com/owner/repo/releases/tag/v0.6.0")), release);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://api.github.com/repos/owner/repo/releases/latest", request.RequestUri!.ToString());
        Assert.Equal("HomeBlaze/0.5.0", string.Join(' ', request.Headers.GetValues("User-Agent")));
        Assert.False(request.Headers.Contains("If-None-Match"));
    }

    [Fact]
    public async Task WhenTheReleaseIsUnchanged_ThenTheETagIsSentAndTheCachedReleaseReturned()
    {
        // Arrange
        var responses = new Queue<HttpResponseMessage>([
            FakeHttpMessageHandler.Json(ReleaseJson, "\"abc\""),
            new HttpResponseMessage(HttpStatusCode.NotModified),
        ]);
        var handler = new FakeHttpMessageHandler(_ => responses.Dequeue());
        var client = CreateClient(handler);
        var first = await client.GetLatestReleaseAsync("owner", "repo", CancellationToken.None);

        // Act
        var second = await client.GetLatestReleaseAsync("owner", "repo", CancellationToken.None);

        // Assert
        Assert.Equal(first, second);
        Assert.Equal("\"abc\"", string.Join(' ', handler.Requests[1].Headers.GetValues("If-None-Match")));
    }

    [Fact]
    public async Task WhenTheRepositoryHasNoRelease_ThenNullIsReturned()
    {
        // Arrange
        var client = CreateClient(new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));

        // Act
        var release = await client.GetLatestReleaseAsync("owner", "repo", CancellationToken.None);

        // Assert
        Assert.Null(release);
    }

    [Fact]
    public async Task WhenTheRateLimitIsExhausted_ThenTheExceptionCarriesTheTimeUntilReset()
    {
        // Arrange
        var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
        response.Headers.Add("x-ratelimit-remaining", "0");
        response.Headers.Add("x-ratelimit-reset", Now.AddMinutes(90).ToUnixTimeSeconds().ToString());
        var client = CreateClient(new FakeHttpMessageHandler(_ => response));

        // Act & Assert
        var exception = await Assert.ThrowsAsync<GitHubRequestException>(() => client.GetLatestReleaseAsync("owner", "repo", CancellationToken.None));
        Assert.Equal(HttpStatusCode.Forbidden, exception.StatusCode);
        Assert.Equal(TimeSpan.FromMinutes(90), exception.RetryAfter);
    }

    [Fact]
    public async Task WhenGitHubAnswersTooManyRequestsWithRetryAfter_ThenTheExceptionCarriesIt()
    {
        // Arrange
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(5));
        var client = CreateClient(new FakeHttpMessageHandler(_ => response));

        // Act & Assert
        var exception = await Assert.ThrowsAsync<GitHubRequestException>(() => client.GetLatestReleaseAsync("owner", "repo", CancellationToken.None));
        Assert.Equal(TimeSpan.FromMinutes(5), exception.RetryAfter);
    }

    [Fact]
    public async Task WhenTheReleaseJsonHasNoTag_ThenAJsonExceptionIsThrown()
    {
        // Arrange
        var client = CreateClient(new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.Json("""{ "name": "x" }""")));

        // Act & Assert
        await Assert.ThrowsAsync<JsonException>(() => client.GetLatestReleaseAsync("owner", "repo", CancellationToken.None));
    }

    [Fact]
    public async Task WhenABranchIsCompared_ThenCommitsBehindAndTheCompareUrlAreRead()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.Json(
            """{ "ahead_by": 12, "behind_by": 0, "html_url": "https://github.com/owner/repo/compare/abc...master" }"""));
        var client = CreateClient(handler);

        // Act
        var comparison = await client.CompareWithBranchAsync("owner", "repo", "abc1234", "master", CancellationToken.None);

        // Assert
        Assert.Equal(new BranchComparison(12, new Uri("https://github.com/owner/repo/compare/abc...master")), comparison);
        Assert.Equal("https://api.github.com/repos/owner/repo/compare/abc1234...master?page=2&per_page=1", Assert.Single(handler.Requests).RequestUri!.ToString());
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
        var release = await client.GetLatestReleaseAsync("owner", "repo", CancellationToken.None);

        // Assert
        Assert.Equal(expectedVersion, release!.Version);
    }

    [Fact]
    public async Task WhenTheReleaseAnswerIsNotAJsonObject_ThenAJsonExceptionIsThrown()
    {
        // Arrange
        var client = CreateClient(new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.Json("[]")));

        // Act & Assert
        await Assert.ThrowsAsync<JsonException>(() => client.GetLatestReleaseAsync("owner", "repo", CancellationToken.None));
    }

    [Fact]
    public async Task WhenTheComparisonHasNoAheadByNumber_ThenAJsonExceptionIsThrown()
    {
        // Arrange
        var client = CreateClient(new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.Json("""{ "ahead_by": null }""")));

        // Act & Assert
        await Assert.ThrowsAsync<JsonException>(() => client.CompareWithBranchAsync("owner", "repo", "abc1234", "master", CancellationToken.None));
    }

    [Fact]
    public async Task WhenTheComparisonIsUnchanged_ThenTheETagIsSentAndTheCachedComparisonReturned()
    {
        // Arrange
        var responses = new Queue<HttpResponseMessage>([
            FakeHttpMessageHandler.Json(
                """{ "ahead_by": 12, "behind_by": 0, "html_url": "https://github.com/owner/repo/compare/abc...master" }""", "\"xyz\""),
            new HttpResponseMessage(HttpStatusCode.NotModified),
        ]);
        var handler = new FakeHttpMessageHandler(_ => responses.Dequeue());
        var client = CreateClient(handler);
        var first = await client.CompareWithBranchAsync("owner", "repo", "abc1234", "master", CancellationToken.None);

        // Act
        var second = await client.CompareWithBranchAsync("owner", "repo", "abc1234", "master", CancellationToken.None);

        // Assert
        Assert.Equal(first, second);
        Assert.Equal("\"xyz\"", string.Join(' ', handler.Requests[1].Headers.GetValues("If-None-Match")));
    }

    [Fact]
    public async Task WhenTheReleaseDisappearsAfterBeingCached_ThenTheCacheIsClearedAndNoETagIsSentNext()
    {
        // Arrange
        var responses = new Queue<HttpResponseMessage>([
            FakeHttpMessageHandler.Json(ReleaseJson, "\"abc\""),
            new HttpResponseMessage(HttpStatusCode.NotFound),
            FakeHttpMessageHandler.Json(ReleaseJson, "\"abc\""),
        ]);
        var handler = new FakeHttpMessageHandler(_ => responses.Dequeue());
        var client = CreateClient(handler);
        await client.GetLatestReleaseAsync("owner", "repo", CancellationToken.None);
        await client.GetLatestReleaseAsync("owner", "repo", CancellationToken.None);

        // Act
        await client.GetLatestReleaseAsync("owner", "repo", CancellationToken.None);

        // Assert
        Assert.False(handler.Requests[2].Headers.Contains("If-None-Match"));
    }

    [Fact]
    public async Task WhenGitHubAnswersTooManyRequestsWithARetryAfterDate_ThenTheExceptionCarriesTheDifferenceToNow()
    {
        // Arrange
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(Now.AddMinutes(10));
        var client = CreateClient(new FakeHttpMessageHandler(_ => response));

        // Act & Assert
        var exception = await Assert.ThrowsAsync<GitHubRequestException>(() => client.GetLatestReleaseAsync("owner", "repo", CancellationToken.None));
        Assert.Equal(TimeSpan.FromMinutes(10), exception.RetryAfter);
    }

    [Fact]
    public async Task WhenGitHubAnswersWithoutAReasonPhrase_ThenTheMessageOmitsIt()
    {
        // Arrange
        var response = new HttpResponseMessage((HttpStatusCode)599);
        var client = CreateClient(new FakeHttpMessageHandler(_ => response));

        // Act & Assert
        var exception = await Assert.ThrowsAsync<GitHubRequestException>(() => client.GetLatestReleaseAsync("owner", "repo", CancellationToken.None));
        Assert.Equal("GitHub answered 599.", exception.Message);
    }

    [Fact]
    public async Task WhenTheRateLimitIsExhausted_ThenTheMessageSaysSo()
    {
        // Arrange
        var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
        response.Headers.Add("x-ratelimit-remaining", "0");
        var client = CreateClient(new FakeHttpMessageHandler(_ => response));

        // Act & Assert
        var exception = await Assert.ThrowsAsync<GitHubRequestException>(() => client.GetLatestReleaseAsync("owner", "repo", CancellationToken.None));
        Assert.Equal("GitHub rate limit reached.", exception.Message);
    }

    [Fact]
    public async Task WhenGitHubAnswersTooManyRequestsWithRetryAfter_ThenTheMessageSaysRateLimited()
    {
        // Arrange
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(5));
        var client = CreateClient(new FakeHttpMessageHandler(_ => response));

        // Act & Assert
        var exception = await Assert.ThrowsAsync<GitHubRequestException>(() => client.GetLatestReleaseAsync("owner", "repo", CancellationToken.None));
        Assert.Equal("GitHub rate limit reached.", exception.Message);
    }

    [Fact]
    public async Task WhenTheCallerTokenIsAlreadyCanceled_ThenOperationCanceledExceptionIsThrown()
    {
        // Arrange
        using var cancellationTokenSource = new CancellationTokenSource();
        cancellationTokenSource.Cancel();
        var client = CreateClient(new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.Json(ReleaseJson)));

        // Act & Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetLatestReleaseAsync("owner", "repo", cancellationTokenSource.Token));
    }
}
