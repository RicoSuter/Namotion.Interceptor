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
    public async Task WhenTheCheckFailsWhileEnabled_ThenTheInstallationIsInError()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var installation = CreateInstallation(CreateBuild(), () => new HttpClient(handler));
        installation.UpdateCheckEnabled = true;

        // Act
        await installation.CheckForUpdatesCoreAsync(CancellationToken.None);

        // Assert
        Assert.Equal(ServiceStatus.Error, installation.Status);
        Assert.StartsWith("GitHub answered 500", installation.StatusMessage);
        Assert.Equal(installation.StatusMessage, installation.UpdateCheckStatus);
        Assert.Equal("Error", installation.IconColor);
    }

    [Fact]
    public async Task WhenTheCheckFailsWhileDisabled_ThenOnlyTheUpdateStatusShowsIt()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var installation = CreateInstallation(CreateBuild(), () => new HttpClient(handler));

        // Act
        await installation.CheckForUpdatesCoreAsync(CancellationToken.None);

        // Assert
        Assert.NotEqual(ServiceStatus.Error, installation.Status);
        Assert.StartsWith("GitHub answered 500", installation.UpdateCheckStatus);
    }

    [Fact]
    public async Task WhenGitHubDoesNotAnswerInTime_ThenTheTimeoutIsReported()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler(_ => throw new TaskCanceledException());
        var installation = CreateInstallation(CreateBuild(), () => new HttpClient(handler));
        installation.UpdateCheckEnabled = true;

        // Act
        await installation.CheckForUpdatesCoreAsync(CancellationToken.None);

        // Assert
        Assert.Equal("GitHub did not answer in time.", installation.UpdateCheckStatus);
        Assert.Equal("GitHub did not answer in time.", installation.StatusMessage);
        Assert.Equal(ServiceStatus.Error, installation.Status);
    }

    [Fact]
    public async Task WhenACheckSucceedsAfterAnError_ThenTheErrorIsCleared()
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
        Assert.Equal(ServiceStatus.Running, installation.Status);
        Assert.Null(installation.StatusMessage);
    }

    [Fact]
    public async Task WhenTheUpdateCheckIsDisabledAfterAnError_ThenTheErrorIsCleared()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var installation = CreateInstallation(CreateBuild(), () => new HttpClient(handler));
        installation.UpdateCheckEnabled = true;
        await installation.CheckForUpdatesCoreAsync(CancellationToken.None);

        // Act
        installation.UpdateCheckEnabled = false;
        await installation.ApplyConfigurationAsync(CancellationToken.None);

        // Assert
        Assert.Equal(ServiceStatus.Running, installation.Status);
    }

    [Fact]
    public void WhenCheckForUpdatesIsCalledTwiceQuickly_ThenTheSecondCallIsRejected()
    {
        // Arrange
        var installation = CreateInstallation(CreateBuild(), () => new HttpClient(new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.Json(ReleaseJson))));
        installation.CheckForUpdates();

        // Act & Assert
        Assert.Throws<InvalidOperationException>(installation.CheckForUpdates);
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
        await installation.StartAsync(CancellationToken.None);
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
        await installation.StartAsync(CancellationToken.None);
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
    public async Task WhenTheCheckIsDisabledAfterAFailedCheckWhileRunning_ThenTheStatusReturnsToRunning()
    {
        // Arrange
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var installation = CreateInstallation(CreateBuild(), () => new HttpClient(handler));
        installation.UpdateCheckEnabled = true;

        await installation.StartAsync(CancellationToken.None);
        try
        {
            installation.CheckForUpdates();
            await AsyncTestHelpers.WaitUntilAsync(() => installation.Status == ServiceStatus.Error, message: "The failed check did not set Error");

            // Act
            installation.UpdateCheckEnabled = false;
            await installation.ApplyConfigurationAsync(CancellationToken.None);

            // Assert
            Assert.Equal(ServiceStatus.Running, installation.Status);
            Assert.Null(installation.StatusMessage);
        }
        finally
        {
            await installation.StopAsync(CancellationToken.None);
        }
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

        await installation.StartAsync(CancellationToken.None);
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

        await installation.StartAsync(CancellationToken.None);
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

    private sealed class BrokenStream : MemoryStream
    {
        public const string Message = "The response ended prematurely.";

        public override int Read(byte[] buffer, int offset, int count) => throw new IOException(Message);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => throw new IOException(Message);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => throw new IOException(Message);
    }
}
