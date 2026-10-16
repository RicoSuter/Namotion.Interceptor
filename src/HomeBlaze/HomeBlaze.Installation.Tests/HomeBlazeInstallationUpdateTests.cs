using System.Diagnostics;
using System.Net;
using HomeBlaze.Abstractions;
using Namotion.Interceptor.Testing;
using NuGet.Versioning;
using Xunit;
using static HomeBlaze.Installation.Tests.FakeHttpMessageHandler;
using static HomeBlaze.Installation.Tests.HomeBlazeInstallationTests;

namespace HomeBlaze.Installation.Tests;

public class HomeBlazeInstallationUpdateTests
{
    [Fact]
    public async Task WhenANewerReleaseExists_ThenTheUpdateIsReported()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.Json(ReleaseJson));
        var installation = CreateInstallation(CreateBuild(), () => new HttpClient(handler));

        // Act
        await installation.CheckForUpdatesCoreAsync(CancellationToken.None);

        // Assert
        Assert.True(installation.UpdateAvailable);
        Assert.Equal("0.6.0", installation.LatestVersion);
        Assert.Equal(new Uri("https://github.com/owner/repo/releases/tag/v0.6.0"), installation.LatestReleaseUrl);
        Assert.NotNull(installation.LastUpdateCheck);
        Assert.Null(installation.UpdateCheckStatus);
        Assert.Equal("Warning", installation.IconColor);
    }

    [Theory]
    [InlineData("0.5.0", true, true)]
    [InlineData("0.6.0", true, false)]
    [InlineData("0.5.0", false, false)]
    [InlineData("not a version", true, null)]
    [InlineData("not a version", false, null)]
    public async Task WhenChecked_ThenUpdateAvailableComparesTheRunningVersionWithTheLatestRelease(string version, bool hasRelease, bool? expected)
    {
        // Arrange
        var handler = new FakeHttpMessageHandler(_ => hasRelease
            ? FakeHttpMessageHandler.Json(ReleaseJson)
            : new HttpResponseMessage(HttpStatusCode.NotFound));
        var installation = CreateInstallation(CreateBuild(version), () => new HttpClient(handler));

        // Act
        await installation.CheckForUpdatesCoreAsync(CancellationToken.None);

        // Assert
        Assert.Equal(expected, installation.UpdateAvailable);
    }

    [Theory]
    [InlineData("0.6.0", "0.5.0", true)]
    [InlineData("0.5.0", "0.5.0", false)]
    [InlineData("0.5.0", "0.6.0-beta.1", false)]
    [InlineData("0.6.0", "0.6.0-beta.1", true)]
    [InlineData("1.0.0.1", "1.0.0", true)]
    [InlineData("not a version", "0.5.0", false)]
    [InlineData("0.6.0+abc", "0.6.0", false)]
    [InlineData("0.4.0", "0.5.0", false)]
    public void WhenVersionsAreCompared_ThenSemanticVersionOrderingApplies(string candidate, string current, bool expected)
    {
        // Act
        var isNewer = HomeBlazeInstallation.IsNewer(candidate, NuGetVersion.Parse(current));

        // Assert
        Assert.Equal(expected, isNewer);
    }

    [Theory]
    [InlineData("1.0.0", null, RepositoryUrl, "Not available for development builds")]
    [InlineData("0.5.0", "v0.5.0", "https://gitlab.com/owner/repo", "Not supported for gitlab.com")]
    [InlineData("0.5.0", "v0.5.0", "https://github.com/owner", "Not available for this repository URL")]
    [InlineData("0.5.0", "v0.5.0", null, "Not available without a repository URL")]
    public void WhenNoCheckCanRunForTheBuild_ThenTheStatusAndCheckForUpdatesSayWhy(string version, string? tag, string? repositoryUrl, string expectedReason)
    {
        // Arrange
        var installation = CreateInstallation(CreateBuild(version, tag, repositoryUrl: repositoryUrl));

        // Act & Assert
        Assert.Equal(expectedReason, installation.UpdateCheckStatus);
        var exception = Assert.Throws<InvalidOperationException>(installation.CheckForUpdates);
        Assert.Equal(expectedReason, exception.Message);
    }

    [Theory]
    [InlineData(null, "master")]
    [InlineData(Commit, null)]
    public void WhenAnEdgeBuildHasNoCommitOrBranch_ThenTheStatusAndCheckForUpdatesSayWhy(string? commit, string? branch)
    {
        // Act
        var installation = CreateInstallation(CreateBuild("edge", tag: null, commit: commit, branch: branch));
        var exception = Record.Exception(installation.CheckForUpdates);

        // Assert
        Assert.Equal("Not available for edge builds without a commit or branch", installation.UpdateCheckStatus);
        Assert.IsType<InvalidOperationException>(exception);
        Assert.Equal("Not available for edge builds without a commit or branch", exception.Message);
    }

    [Fact]
    public async Task WhenAnEdgeBuildIsBehindItsBranch_ThenTheUpdateIsReported()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.Json(ComparisonJson(aheadBy: 3, behindBy: 0)));
        var installation = CreateInstallation(CreateBuild("edge", tag: null), () => new HttpClient(handler));

        // Act
        var retryDelay = await installation.CheckForUpdatesCoreAsync(CancellationToken.None);

        // Assert
        Assert.Null(retryDelay);
        Assert.Equal($"https://api.github.com/repos/owner/repo/compare/{Commit}...master?per_page=1&page=2", Assert.Single(handler.Requests).RequestUri!.AbsoluteUri);
        Assert.True(installation.UpdateAvailable);
        Assert.Equal(3, installation.CommitsBehind);
        Assert.Equal(new Uri(CompareUrl), installation.CompareUrl);
        Assert.Null(installation.LatestVersion);
        Assert.NotNull(installation.LastUpdateCheck);
        Assert.Null(installation.UpdateCheckStatus);
        Assert.Equal("Warning", installation.IconColor);
    }

    [Fact]
    public async Task WhenAnEdgeBuildIsTheHeadOfItsBranch_ThenNoUpdateIsReported()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.Json(ComparisonJson(aheadBy: 0, behindBy: 0)));
        var installation = CreateInstallation(CreateBuild("edge", tag: null), () => new HttpClient(handler));

        // Act
        await installation.CheckForUpdatesCoreAsync(CancellationToken.None);

        // Assert
        Assert.False(installation.UpdateAvailable);
        Assert.Equal(0, installation.CommitsBehind);
        Assert.Null(installation.UpdateCheckStatus);
        Assert.Null(installation.IconColor);
    }

    [Theory]
    [InlineData(2, 1, true)]
    [InlineData(0, 1, false)]
    public async Task WhenTheCommitOfAnEdgeBuildIsNotOnItsBranch_ThenOnlyTheUpdateCheckStatusReportsIt(int aheadBy, int behindBy, bool expectedUpdateAvailable)
    {
        // Arrange
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.Json(ComparisonJson(aheadBy, behindBy)));
        var installation = CreateInstallation(CreateBuild("edge", tag: null), () => new HttpClient(handler));
        installation.Status = ServiceStatus.Running;

        // Act
        var retryDelay = await installation.CheckForUpdatesCoreAsync(CancellationToken.None);

        // Assert
        Assert.Null(retryDelay);
        Assert.Equal(expectedUpdateAvailable, installation.UpdateAvailable);
        Assert.Equal(aheadBy, installation.CommitsBehind);
        Assert.Equal("Commit b1b7c2f is not on master.", installation.UpdateCheckStatus);
        Assert.Equal(ServiceStatus.Running, installation.Status);
        Assert.Null(installation.StatusMessage);
    }

    [Fact]
    public async Task WhenTheCommitOfAnEdgeBuildIsOnItsBranchAgain_ThenTheUpdateCheckStatusIsCleared()
    {
        // Arrange
        var responses = new Queue<HttpResponseMessage>([
            FakeHttpMessageHandler.Json(ComparisonJson(aheadBy: 2, behindBy: 1)),
            FakeHttpMessageHandler.Json(ComparisonJson(aheadBy: 1, behindBy: 0)),
        ]);
        var handler = new FakeHttpMessageHandler(_ => responses.Dequeue());
        var installation = CreateInstallation(CreateBuild("edge", tag: null), () => new HttpClient(handler));
        await installation.CheckForUpdatesCoreAsync(CancellationToken.None);

        // Act
        await installation.CheckForUpdatesCoreAsync(CancellationToken.None);

        // Assert
        Assert.Null(installation.UpdateCheckStatus);
        Assert.Equal(1, installation.CommitsBehind);
    }

    [Fact]
    public async Task WhenAnEdgeCheckFails_ThenTheLastComparisonIsKept()
    {
        // Arrange
        var responses = new Queue<HttpResponseMessage>([
            FakeHttpMessageHandler.Json(ComparisonJson(aheadBy: 3, behindBy: 0)),
            new HttpResponseMessage(HttpStatusCode.InternalServerError),
        ]);
        var handler = new FakeHttpMessageHandler(_ => responses.Dequeue());
        var installation = CreateInstallation(CreateBuild("edge", tag: null), () => new HttpClient(handler));
        await installation.CheckForUpdatesCoreAsync(CancellationToken.None);

        // Act
        await installation.CheckForUpdatesCoreAsync(CancellationToken.None);

        // Assert
        Assert.StartsWith("GitHub answered 500", installation.UpdateCheckStatus);
        Assert.True(installation.UpdateAvailable);
        Assert.Equal(3, installation.CommitsBehind);
        Assert.Equal(new Uri(CompareUrl), installation.CompareUrl);
    }

    [Fact]
    public async Task WhenGitHubDoesNotKnowTheCommitOfAnEdgeBuild_ThenOnlyTheUpdateCheckStatusReportsIt()
    {
        // Arrange
        var responses = new Queue<HttpResponseMessage>([
            FakeHttpMessageHandler.Json(ComparisonJson(aheadBy: 3, behindBy: 0)),
            new HttpResponseMessage(HttpStatusCode.NotFound),
        ]);
        var handler = new FakeHttpMessageHandler(_ => responses.Dequeue());
        var installation = CreateInstallation(CreateBuild("edge", tag: null), () => new HttpClient(handler));
        installation.Status = ServiceStatus.Running;
        await installation.CheckForUpdatesCoreAsync(CancellationToken.None);

        // Act
        var retryDelay = await installation.CheckForUpdatesCoreAsync(CancellationToken.None);

        // Assert
        Assert.Null(retryDelay);
        Assert.Null(installation.UpdateAvailable);
        Assert.Null(installation.CommitsBehind);
        Assert.Null(installation.CompareUrl);
        Assert.Equal("GitHub could not compare commit b1b7c2f with master.", installation.UpdateCheckStatus);
        Assert.Equal(ServiceStatus.Running, installation.Status);
    }

    [Fact]
    public async Task WhenTheComparisonOfAnEdgeBuildIsRateLimited_ThenTheLastResultIsKeptAndGitHubsWaitApplies()
    {
        // Arrange
        var responses = new Queue<HttpResponseMessage>([
            FakeHttpMessageHandler.Json(ComparisonJson(aheadBy: 3, behindBy: 0)),
            CreateTooManyRequests(TimeSpan.FromMinutes(30)),
        ]);
        var handler = new FakeHttpMessageHandler(_ => responses.Dequeue());
        var installation = CreateInstallation(CreateBuild("edge", tag: null), () => new HttpClient(handler), new TestTimeProvider(DateTimeOffset.UtcNow));
        await installation.CheckForUpdatesCoreAsync(CancellationToken.None);
        installation.Status = ServiceStatus.Running;

        // Act
        var retryDelay = await installation.CheckForUpdatesCoreAsync(CancellationToken.None);

        // Assert
        Assert.Equal(UpdateSchedule.InitialBackoff, retryDelay);
        Assert.Equal("GitHub rate limit reached.", installation.UpdateCheckStatus);
        Assert.True(installation.UpdateAvailable);
        Assert.Equal(3, installation.CommitsBehind);
        var exception = Assert.Throws<InvalidOperationException>(installation.CheckForUpdates);
        Assert.Equal("GitHub asked to wait 30 more minutes.", exception.Message);
    }

    [Fact]
    public async Task WhenTheCheckFails_ThenOnlyTheUpdateCheckStatusReportsIt()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var installation = CreateInstallation(CreateBuild(), () => new HttpClient(handler));
        installation.Status = ServiceStatus.Running;

        // Act
        await installation.CheckForUpdatesCoreAsync(CancellationToken.None);

        // Assert
        Assert.StartsWith("GitHub answered 500", installation.UpdateCheckStatus);
        Assert.Equal(ServiceStatus.Running, installation.Status);
        Assert.Null(installation.StatusMessage);
        Assert.Null(installation.IconColor);
    }

    [Fact]
    public async Task WhenGitHubDoesNotAnswerInTime_ThenTheTimeoutIsReported()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new UnreachableException();
        });

        // The timeout of HttpClient is off, so only the request timeout of the release client can end the request.
        var installation = CreateInstallation(
            CreateBuild(),
            () => new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan },
            requestTimeout: TimeSpan.FromMilliseconds(50));

        // Act
        await installation.CheckForUpdatesCoreAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));

        // Assert
        Assert.Equal("GitHub did not answer in time.", installation.UpdateCheckStatus);
    }

    [Fact]
    public async Task WhenACheckSucceedsAfterAnError_ThenTheUpdateCheckStatusIsCleared()
    {
        // Arrange
        var responses = new Queue<HttpResponseMessage>([
            new HttpResponseMessage(HttpStatusCode.InternalServerError),
            FakeHttpMessageHandler.Json(ReleaseJson),
        ]);
        var handler = new FakeHttpMessageHandler(_ => responses.Dequeue());
        var installation = CreateInstallation(CreateBuild(), () => new HttpClient(handler));
        await installation.CheckForUpdatesCoreAsync(CancellationToken.None);

        // Act
        await installation.CheckForUpdatesCoreAsync(CancellationToken.None);

        // Assert
        Assert.Null(installation.UpdateCheckStatus);
        Assert.True(installation.UpdateAvailable);
    }

    [Fact]
    public async Task WhenCheckForUpdatesIsCalledTwiceQuickly_ThenTheSecondCallIsRejected()
    {
        // Arrange
        var installation = CreateInstallation(CreateBuild(), () => new HttpClient(new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.Json(ReleaseJson))));
        await StartAndWaitUntilRunningAsync(installation);
        try
        {
            installation.CheckForUpdates();

            // Act & Assert
            Assert.Throws<InvalidOperationException>(installation.CheckForUpdates);
        }
        finally
        {
            await installation.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenCheckForUpdatesIsCalledWhileStopped_ThenItIsRejectedWithoutUsingTheThrottle()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.Json(ReleaseJson));
        var installation = CreateInstallation(CreateBuild(), () => new HttpClient(handler));

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(installation.CheckForUpdates);
        Assert.Equal("The installation is not running.", exception.Message);

        await StartAndWaitUntilRunningAsync(installation);
        try
        {
            installation.CheckForUpdates();
            await AsyncTestHelpers.WaitUntilAsync(
                () => installation.UpdateAvailable is not null, message: "The update check did not run after the start");
        }
        finally
        {
            await installation.StopAsync(CancellationToken.None);
        }

        Assert.True(installation.UpdateAvailable);
    }

    [Fact]
    public async Task WhenACheckThrowsAnUnexpectedException_ThenTheUpdateLoopKeepsRunning()
    {
        // Arrange
        var callCount = 0;
        var handler = new FakeHttpMessageHandler(_ => Interlocked.Increment(ref callCount) == 1
            ? throw new InvalidCastException("The first request fails")
            : FakeHttpMessageHandler.Json(ReleaseJson));
        var timeProvider = new TestTimeProvider(DateTimeOffset.UtcNow);
        var installation = CreateInstallation(CreateBuild(), () => new HttpClient(handler), timeProvider);

        // Act
        await StartAndWaitUntilRunningAsync(installation);
        try
        {
            installation.CheckForUpdates();
            await AsyncTestHelpers.WaitUntilAsync(
                () => installation.UpdateCheckStatus == "The first request fails", message: "The failing check did not run");

            timeProvider.Advance(UpdateSchedule.ManualCheckThrottle);
            installation.CheckForUpdates();
            await AsyncTestHelpers.WaitUntilAsync(
                () => installation.UpdateAvailable is not null, message: "The update loop stopped after the failure");
        }
        finally
        {
            await installation.StopAsync(CancellationToken.None);
        }

        // Assert
        Assert.True(installation.UpdateAvailable);
        Assert.Null(installation.UpdateCheckStatus);
    }

    [Fact]
    public async Task WhenTheAnswerBreaksOffWhileReading_ThenTheFailureIsReported()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BrokenStream()) });
        var installation = CreateInstallation(CreateBuild(), () => new HttpClient(handler));

        // Act
        await installation.CheckForUpdatesCoreAsync(CancellationToken.None);

        // Assert
        Assert.Equal(BrokenStream.Message, installation.UpdateCheckStatus);
    }

    [Fact]
    public async Task WhenTheIntervalIsShortenedAfterACheck_ThenTheNextCheckFollowsTheNewInterval()
    {
        // Arrange
        var requestCount = 0;
        var handler = new FakeHttpMessageHandler(_ =>
        {
            Interlocked.Increment(ref requestCount);
            return FakeHttpMessageHandler.Json(ReleaseJson);
        });
        var timeProvider = new TestTimeProvider(DateTimeOffset.UtcNow);
        var installation = CreateInstallation(CreateBuild(), () => new HttpClient(handler), timeProvider);
        installation.UpdateCheckInterval = TimeSpan.FromHours(48);

        await StartAndWaitUntilRunningAsync(installation);
        try
        {
            installation.CheckForUpdates();
            await AsyncTestHelpers.WaitUntilAsync(() => installation.UpdateAvailable is not null, message: "The first check did not run");

            // Act
            timeProvider.Advance(TimeSpan.FromHours(25));
            installation.UpdateCheckInterval = TimeSpan.FromHours(24);
            await installation.ApplyConfigurationAsync(CancellationToken.None);

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(() => Volatile.Read(ref requestCount) == 2, message: "The shorter interval was not applied");
        }
        finally
        {
            await installation.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenTheWallClockJumpsBack_ThenTheScheduledCheckStillRuns()
    {
        // Arrange
        var requestCount = 0;
        var handler = new FakeHttpMessageHandler(_ =>
        {
            Interlocked.Increment(ref requestCount);
            return FakeHttpMessageHandler.Json(ReleaseJson);
        });
        var timeProvider = new TestTimeProvider(DateTimeOffset.UtcNow);
        var installation = CreateInstallation(CreateBuild(), () => new HttpClient(handler), timeProvider);

        await StartAndWaitUntilRunningAsync(installation);
        try
        {
            installation.CheckForUpdates();
            await AsyncTestHelpers.WaitUntilAsync(() => installation.UpdateAvailable is not null, message: "The first check did not run");

            // Act
            timeProvider.UtcNow -= TimeSpan.FromDays(365);
            timeProvider.Advance(TimeSpan.FromHours(25));
            await installation.ApplyConfigurationAsync(CancellationToken.None);

            // Assert
            await AsyncTestHelpers.WaitUntilAsync(() => Volatile.Read(ref requestCount) == 2, message: "The check did not run after the wall clock jumped back");
        }
        finally
        {
            await installation.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenGitHubAskedToWait_ThenAManualCheckIsRejectedUntilThen()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler(_ => CreateTooManyRequests(TimeSpan.FromMinutes(30)));
        var timeProvider = new TestTimeProvider(DateTimeOffset.UtcNow);
        var installation = CreateInstallation(CreateBuild(), () => new HttpClient(handler), timeProvider);
        await installation.CheckForUpdatesCoreAsync(CancellationToken.None);
        installation.Status = ServiceStatus.Running;
        timeProvider.Advance(TimeSpan.FromMinutes(10));

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(installation.CheckForUpdates);
        Assert.Equal("GitHub asked to wait 20 more minutes.", exception.Message);
    }

    [Fact]
    public async Task WhenGitHubAsksToWaitAfterAManualCheckWasAccepted_ThenTheUpdateLoopDoesNotRunIt()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler(_ => CreateTooManyRequests(TimeSpan.FromMinutes(30)));
        var installation = CreateInstallation(CreateBuild(), () => new HttpClient(handler), new TestTimeProvider(DateTimeOffset.UtcNow));
        installation.Status = ServiceStatus.Running;
        installation.CheckForUpdates();

        // The check that was in flight when the manual check was accepted finishes with a wait.
        await installation.CheckForUpdatesCoreAsync(CancellationToken.None);

        // Act
        var mayRun = installation.TryTakeManualCheckRequest();

        // Assert
        Assert.False(mayRun);
        Assert.Equal("GitHub rate limit reached.", installation.UpdateCheckStatus);
    }

    [Fact]
    public async Task WhenTheWaitGitHubAskedForHasPassed_ThenAManualCheckIsAccepted()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler(_ => CreateTooManyRequests(TimeSpan.FromMinutes(30)));
        var timeProvider = new TestTimeProvider(DateTimeOffset.UtcNow);
        var installation = CreateInstallation(CreateBuild(), () => new HttpClient(handler), timeProvider);
        await installation.CheckForUpdatesCoreAsync(CancellationToken.None);
        installation.Status = ServiceStatus.Running;
        timeProvider.Advance(TimeSpan.FromMinutes(30));

        // Act
        var exception = Record.Exception(installation.CheckForUpdates);

        // Assert
        Assert.Null(exception);
    }

    [Fact]
    public async Task WhenACheckSucceedsAfterGitHubAskedToWait_ThenAManualCheckIsAccepted()
    {
        // Arrange
        var responses = new Queue<HttpResponseMessage>([
            CreateTooManyRequests(TimeSpan.FromMinutes(30)),
            FakeHttpMessageHandler.Json(ReleaseJson),
        ]);
        var handler = new FakeHttpMessageHandler(_ => responses.Dequeue());
        var installation = CreateInstallation(CreateBuild(), () => new HttpClient(handler), new TestTimeProvider(DateTimeOffset.UtcNow));
        await installation.CheckForUpdatesCoreAsync(CancellationToken.None);
        await installation.CheckForUpdatesCoreAsync(CancellationToken.None);
        installation.Status = ServiceStatus.Running;

        // Act
        var exception = Record.Exception(installation.CheckForUpdates);

        // Assert
        Assert.Null(exception);
    }

    private static HttpResponseMessage CreateTooManyRequests(TimeSpan retryAfter)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(retryAfter);
        return response;
    }

    // ExecuteAsync runs on the thread pool, so the status is still Stopped when StartAsync returns.
    private static async Task StartAndWaitUntilRunningAsync(HomeBlazeInstallation installation)
    {
        await installation.StartAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(
            () => installation.Status != ServiceStatus.Stopped, message: "The installation did not start");
    }

    private sealed class BrokenStream : MemoryStream
    {
        public const string Message = "The response ended prematurely.";

        public override int Read(byte[] buffer, int offset, int count) => throw new IOException(Message);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => throw new IOException(Message);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => throw new IOException(Message);
    }
}
