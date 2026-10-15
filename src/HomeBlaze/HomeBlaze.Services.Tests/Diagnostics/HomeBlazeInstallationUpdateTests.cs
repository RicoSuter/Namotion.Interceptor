using System.Net;
using HomeBlaze.Abstractions;
using HomeBlaze.Services.Diagnostics;
using Namotion.Interceptor.Testing;
using Xunit;
using static HomeBlaze.Services.Tests.Diagnostics.HomeBlazeInstallationTests;

namespace HomeBlaze.Services.Tests.Diagnostics;

public class HomeBlazeInstallationUpdateTests
{
    private const string ReleaseJson = """
        { "tag_name": "v0.6.0", "published_at": "2026-10-20T08:00:00Z", "html_url": "https://github.com/owner/repo/releases/tag/v0.6.0" }
        """;

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

    [Fact]
    public async Task WhenAnEdgeBuildIsBehind_ThenCommitsBehindAndChangesAreReported()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler(request => request.RequestUri!.AbsolutePath.Contains("/compare/")
            ? FakeHttpMessageHandler.Json("""{ "ahead_by": 3, "html_url": "https://github.com/owner/repo/compare/x...master" }""")
            : FakeHttpMessageHandler.Json(ReleaseJson));
        var installation = CreateInstallation(CreateBuild("edge", tag: null), () => new HttpClient(handler));

        // Act
        await installation.CheckForUpdatesCoreAsync(CancellationToken.None);

        // Assert
        Assert.True(installation.UpdateAvailable);
        Assert.Equal(3, installation.CommitsBehind);
        Assert.Equal(new Uri("https://github.com/owner/repo/compare/x...master"), installation.ChangesUrl);
        Assert.Contains(handler.Requests, request => request.RequestUri!.AbsolutePath.EndsWith($"/compare/{Commit}...master"));
    }

    [Fact]
    public async Task WhenTheCheckFails_ThenOnlyTheUpdateCheckStatusReportsIt()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var installation = CreateInstallation(CreateBuild(), () => new HttpClient(handler));
        installation.UpdateCheckEnabled = true;
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
        var handler = new FakeHttpMessageHandler(_ => throw new TaskCanceledException());
        var installation = CreateInstallation(CreateBuild(), () => new HttpClient(handler));

        // Act
        await installation.CheckForUpdatesCoreAsync(CancellationToken.None);

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
        installation.UpdateCheckEnabled = true;
        await installation.CheckForUpdatesCoreAsync(CancellationToken.None);

        // Act
        await installation.CheckForUpdatesCoreAsync(CancellationToken.None);

        // Assert
        Assert.Null(installation.UpdateCheckStatus);
        Assert.True(installation.UpdateAvailable);
    }

    [Theory]
    [InlineData("develop", "develop")]
    [InlineData(null, "master")]
    public void WhenCreated_ThenTheComparedBranchIsTheBuildBranchOrMaster(string? branch, string expected)
    {
        // Arrange
        var build = CreateBuild("edge", tag: null) with { Branch = branch };

        // Act
        var installation = CreateInstallation(build);

        // Assert
        Assert.Equal(expected, installation.ComparedBranch);
    }

    [Fact]
    public async Task WhenAnEdgeBuildHasNoBranch_ThenItIsComparedWithMaster()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler(request => request.RequestUri!.AbsolutePath.Contains("/compare/")
            ? FakeHttpMessageHandler.Json("""{ "ahead_by": 0, "html_url": "https://github.com/owner/repo/compare/x...master" }""")
            : FakeHttpMessageHandler.Json(ReleaseJson));
        var installation = CreateInstallation(CreateBuild("edge", tag: null) with { Branch = null }, () => new HttpClient(handler));

        // Act
        await installation.CheckForUpdatesCoreAsync(CancellationToken.None);

        // Assert
        Assert.Contains(handler.Requests, request => request.RequestUri!.AbsolutePath.EndsWith($"/compare/{Commit}...master"));
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
    }

    [Fact]
    public void WhenCheckForUpdatesIsCalledOnADevelopmentBuild_ThenTheReasonIsThrown()
    {
        // Arrange
        var installation = CreateInstallation(CreateBuild("1.0.0", tag: null));

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(installation.CheckForUpdates);
        Assert.Equal("Not available for development builds", exception.Message);
    }

    [Fact]
    public async Task WhenStartedWithACheckRequested_ThenTheCheckRunsInTheBackground()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.Json(ReleaseJson));
        var installation = CreateInstallation(CreateBuild(), () => new HttpClient(handler));

        // Act
        await StartAndWaitUntilRunningAsync(installation);
        try
        {
            installation.CheckForUpdates();
            await AsyncTestHelpers.WaitUntilAsync(
                () => installation.UpdateAvailable is not null, message: "The update check did not run");
        }
        finally
        {
            await installation.StopAsync(CancellationToken.None);
        }

        // Assert
        Assert.True(installation.UpdateAvailable);
        Assert.Equal(ServiceStatus.Stopped, installation.Status);
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
    public void WhenCheckForUpdatesIsCalledForARepositoryOutsideGitHub_ThenTheReasonIsThrown()
    {
        // Arrange
        var installation = CreateInstallation(CreateBuild(repositoryUrl: "https://gitlab.com/owner/repo"), () => new HttpClient(new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.Json(ReleaseJson))));

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(installation.CheckForUpdates);
        Assert.Equal("Not supported for gitlab.com", exception.Message);
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
        installation.UpdateCheckEnabled = true;
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
        installation.UpdateCheckEnabled = true;

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
    public async Task WhenACheckWasRequestedWhileTheLoopStopped_ThenTheNextStartDoesNotRunIt()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.Json(ReleaseJson));
        var installation = CreateInstallation(CreateBuild(), () => new HttpClient(handler));

        // The update loop has ended but the status is still Running, so the request is accepted and left behind.
        installation.Status = ServiceStatus.Running;
        installation.CheckForUpdates();
        installation.Status = ServiceStatus.Stopped;
        Assert.True(installation.HasPendingUpdateWakeUp);

        // Act
        await StartAndWaitUntilRunningAsync(installation);
        try
        {
            // Assert
            Assert.False(installation.HasPendingUpdateWakeUp);
            Assert.Empty(handler.Requests);
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
        installation.CheckForUpdates();

        // Assert
        Assert.True(installation.HasPendingUpdateWakeUp);
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
        installation.CheckForUpdates();

        // Assert
        Assert.True(installation.HasPendingUpdateWakeUp);
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
