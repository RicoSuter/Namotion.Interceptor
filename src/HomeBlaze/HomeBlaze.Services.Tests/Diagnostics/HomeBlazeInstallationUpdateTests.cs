using System.Net;
using HomeBlaze.Abstractions;
using HomeBlaze.Services.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
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
        var timeProvider = new SettableTimeProvider(DateTimeOffset.UtcNow);
        var installation = new HomeBlazeInstallation(
            CreateBuild(), "Production", () => new HttpClient(handler), new DeveloperModeService(), null, timeProvider, NullLogger.Instance);

        // Act
        await installation.StartAsync(CancellationToken.None);
        try
        {
            installation.CheckForUpdates();
            await AsyncTestHelpers.WaitUntilAsync(
                () => installation.UpdateCheckStatus == "The first request fails", message: "The failing check did not run");

            timeProvider.UtcNow += UpdateSchedule.ManualCheckThrottle;
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

    private sealed class SettableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private long _utcTicks = now.UtcTicks;

        public DateTimeOffset UtcNow
        {
            get => new(Interlocked.Read(ref _utcTicks), TimeSpan.Zero);
            set => Interlocked.Exchange(ref _utcTicks, value.UtcTicks);
        }

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
