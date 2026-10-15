using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using HomeBlaze.Services.Diagnostics;
using Xunit;
using static HomeBlaze.Services.Tests.Diagnostics.FakeHttpMessageHandler;

namespace HomeBlaze.Services.Tests.Diagnostics;

public class GitHubReleaseClientTests
{
    private const string RateLimitMessage = "GitHub rate limit reached.";

    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private static GitHubReleaseClient CreateClient(FakeHttpMessageHandler handler, string repositoryUrl = "https://github.com/owner/repo") =>
        GitHubReleaseClient.TryCreate(repositoryUrl, () => new HttpClient(handler), "HomeBlaze/0.5.0", new TestTimeProvider(Now))!;

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
    [InlineData("https://gitlab.com/owner/repo")]
    [InlineData("https://github.com/owner")]
    [InlineData(null)]
    public void WhenTheUrlIsNotAGitHubRepository_ThenNoClientIsCreated(string? repositoryUrl)
    {
        // Act
        var client = GitHubReleaseClient.TryCreate(repositoryUrl, () => throw new InvalidOperationException(), "HomeBlaze/0.5.0", TimeProvider.System);

        // Assert
        Assert.Null(client);
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

    [Fact]
    public async Task WhenTheCallerTokenIsAlreadyCanceled_ThenOperationCanceledExceptionIsThrown()
    {
        // Arrange
        using var cancellationTokenSource = new CancellationTokenSource();
        cancellationTokenSource.Cancel();
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
