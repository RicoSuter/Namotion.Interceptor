using System.Net;
using HomeBlaze.Abstractions;
using HomeBlaze.Services.Diagnostics;
using Namotion.Interceptor.Testing;
using Xunit;
using static HomeBlaze.Services.Tests.Diagnostics.FakeHttpMessageHandler;
using static HomeBlaze.Services.Tests.Diagnostics.HomeBlazeInstallationTests;

namespace HomeBlaze.Services.Tests.Diagnostics;

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
        var isNewer = HomeBlazeInstallation.IsNewer(candidate, current);

        // Assert
        Assert.Equal(expected, isNewer);
    }

    [Theory]
    [InlineData("1.0.0", null, RepositoryUrl, "Not available for development builds")]
    [InlineData("edge", null, RepositoryUrl, "Not available for edge builds")]
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
