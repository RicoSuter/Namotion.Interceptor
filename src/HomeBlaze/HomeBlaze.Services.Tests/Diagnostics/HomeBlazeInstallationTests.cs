using System.Net;
using HomeBlaze.Abstractions;
using HomeBlaze.Services.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor;
using Namotion.Interceptor.Testing;
using Xunit;

namespace HomeBlaze.Services.Tests.Diagnostics;

public class HomeBlazeInstallationTests
{
    internal const string Commit = "b1b7c2f4568b7a83baf8d041afaab8f2214fb6cd";
    internal const string RepositoryUrl = "https://github.com/owner/repo";

    internal static BuildInformation CreateBuild(string version = "0.5.0", string? tag = "v0.5.0", string? commit = Commit, string? repositoryUrl = RepositoryUrl) =>
        new(version, commit, repositoryUrl, tag, Branch: "master", CommitDate: null, BuildTime: null, RunUrl: null);

    internal static HomeBlazeInstallation CreateInstallation(
        BuildInformation build,
        Func<HttpClient>? createHttpClient = null,
        TimeProvider? timeProvider = null,
        DeveloperModeService? developerMode = null) =>
        new(build, "Production", createHttpClient ?? CreateNoReleaseClient, developerMode ?? new DeveloperModeService(), timeProvider ?? TimeProvider.System, NullLogger.Instance);

    private static HttpClient CreateNoReleaseClient() =>
        new(new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));

    [Theory]
    [InlineData("0.5.0", "v0.5.0", InstallationChannel.Release)]
    [InlineData("edge", null, InstallationChannel.Edge)]
    [InlineData("1.0.0", null, InstallationChannel.Development)]
    public void WhenCreated_ThenTheChannelFollowsTagAndVersion(string version, string? tag, InstallationChannel expected)
    {
        // Act
        var installation = CreateInstallation(CreateBuild(version, tag));

        // Assert
        Assert.Equal(expected, installation.Channel);
    }

    [Fact]
    public void WhenAReleaseIsCreated_ThenTheDefaultsAndLinksAreSet()
    {
        // Act
        var installation = CreateInstallation(CreateBuild());

        // Assert
        Assert.Equal("Installation", installation.Title);
        Assert.True(installation.UpdateCheckEnabled);
        Assert.Equal("0.5.0", installation.Version);
        Assert.Equal(new Uri($"{RepositoryUrl}/commit/{Commit}"), installation.CommitUrl);
        Assert.Equal(new Uri($"{RepositoryUrl}/releases/tag/v0.5.0"), installation.VersionUrl);
        Assert.Equal("Production", installation.EnvironmentName);
        Assert.Equal(ServiceStatus.Stopped, installation.Status);
        Assert.Null(installation.UpdateCheckStatus);
        Assert.Null(installation.IconColor);
    }

    [Fact]
    public void WhenADevelopmentBuildIsCreated_ThenTheVersionLinksToTheCommit()
    {
        // Act
        var installation = CreateInstallation(CreateBuild("1.0.0", tag: null));

        // Assert
        Assert.Equal(new Uri($"{RepositoryUrl}/commit/{Commit}"), installation.VersionUrl);
    }

    [Fact]
    public void WhenDeveloperModeIsOnAtCreation_ThenItIsReported()
    {
        // Arrange
        var developerMode = new DeveloperModeService();
        developerMode.SetEnabled(true);

        // Act
        var installation = CreateInstallation(CreateBuild(), developerMode: developerMode);

        // Assert
        Assert.True(installation.IsDeveloperMode);
    }

    [Fact]
    public void WhenPolled_ThenTheDataVolumeAndLastUpdatedAreSet()
    {
        // Arrange
        var installation = CreateInstallation(CreateBuild());
        ((IInterceptorSubject)installation).Context.AddService<IDataDirectoryProvider>(new DataDirectoryProvider(Path.GetTempPath()));

        // Act
        installation.Poll();

        // Assert
        Assert.Equal(Path.GetTempPath(), installation.DataDirectory);
        Assert.NotNull(installation.DataVolumeTotalSize);
        Assert.NotNull(installation.LastUpdated);
    }

    [Fact]
    public async Task WhenStartedAndStopped_ThenStatusAndDeveloperModeFollowTheLifecycle()
    {
        // Arrange
        var developerMode = new DeveloperModeService();
        var installation = CreateInstallation(CreateBuild(), developerMode: developerMode);

        // Act
        await installation.StartAsync(CancellationToken.None);
        try
        {
            await AsyncTestHelpers.WaitUntilAsync(() => installation.Status == ServiceStatus.Running, message: "The installation did not start");
            developerMode.SetEnabled(true);
            await AsyncTestHelpers.WaitUntilAsync(() => installation.IsDeveloperMode, message: "Developer mode was not reported");
        }
        finally
        {
            await installation.StopAsync(CancellationToken.None);
        }

        developerMode.SetEnabled(false);

        // Assert
        Assert.Equal(ServiceStatus.Stopped, installation.Status);
        Assert.True(installation.IsDeveloperMode);
    }

    [Fact]
    public async Task WhenAPollFails_ThenTheNextPollStillRuns()
    {
        // Arrange
        var dataDirectoryProvider = new FailingOnceDataDirectoryProvider(Path.GetTempPath());
        var installation = CreateInstallation(CreateBuild());
        ((IInterceptorSubject)installation).Context.AddService<IDataDirectoryProvider>(dataDirectoryProvider);
        installation.PollingInterval = TimeSpan.FromSeconds(1);

        // Act
        await installation.StartAsync(CancellationToken.None);
        try
        {
            await AsyncTestHelpers.WaitUntilAsync(() => installation.LastUpdated is not null, message: "No poll succeeded after the failure");
        }
        finally
        {
            await installation.StopAsync(CancellationToken.None);
        }

        // Assert
        Assert.True(dataDirectoryProvider.CallCount >= 2);
        Assert.Equal(Path.GetTempPath(), installation.DataDirectory);
        Assert.NotNull(installation.DataVolumeTotalSize);
    }

    private sealed class DataDirectoryProvider(string dataDirectory) : IDataDirectoryProvider
    {
        public string DataDirectory => dataDirectory;
    }

    private sealed class FailingOnceDataDirectoryProvider(string dataDirectory) : IDataDirectoryProvider
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public string DataDirectory =>
            Interlocked.Increment(ref _callCount) == 1
                ? throw new InvalidCastException("The first read fails")
                : dataDirectory;
    }
}
