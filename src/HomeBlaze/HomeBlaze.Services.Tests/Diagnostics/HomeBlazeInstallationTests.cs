using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Services.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Testing;
using Xunit;

namespace HomeBlaze.Services.Tests.Diagnostics;

[InterceptorSubject]
public partial class TestMonitoredService : IMonitoredService
{
    [State]
    public partial ServiceStatus Status { get; set; }

    [State]
    public partial string? StatusMessage { get; set; }
}

public class HomeBlazeInstallationTests
{
    internal const string Commit = "b1b7c2f4568b7a83baf8d041afaab8f2214fb6cd";
    internal const string RepositoryUrl = "https://github.com/owner/repo";

    internal static BuildInformation CreateBuild(string version = "0.5.0", string? tag = "v0.5.0", string? commit = Commit, string? repositoryUrl = RepositoryUrl) =>
        new(version, commit, repositoryUrl, tag, Branch: "master", CommitDate: null, BuildTime: null, RunUrl: null, ImageTags: null);

    internal static HomeBlazeInstallation CreateInstallation(
        BuildInformation build,
        Func<HttpClient>? createHttpClient = null,
        DeveloperModeService? developerMode = null,
        IListeningUrlsProvider? listeningUrlsProvider = null) =>
        CreateInstallation(build, createHttpClient, TimeProvider.System, developerMode, listeningUrlsProvider);

    internal static HomeBlazeInstallation CreateInstallation(
        BuildInformation build,
        Func<HttpClient>? createHttpClient,
        TimeProvider timeProvider,
        DeveloperModeService? developerMode = null,
        IListeningUrlsProvider? listeningUrlsProvider = null) =>
        new(build, "Production", createHttpClient, developerMode ?? new DeveloperModeService(), listeningUrlsProvider, timeProvider, NullLogger.Instance);

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
    public void WhenAReleaseIsCreated_ThenLinksPointToTheReleaseAndTheCommit()
    {
        // Act
        var installation = CreateInstallation(CreateBuild(), () => new HttpClient(new FakeHttpMessageHandler(_ => new HttpResponseMessage())));

        // Assert
        Assert.Equal("Installation", installation.Title);
        Assert.Equal("0.5.0", installation.Version);
        Assert.Equal(new Uri($"{RepositoryUrl}/commit/{Commit}"), installation.CommitUrl);
        Assert.Equal(new Uri($"{RepositoryUrl}/releases/tag/v0.5.0"), installation.VersionUrl);
        Assert.Equal("Production", installation.EnvironmentName);
        Assert.Equal(ServiceStatus.Stopped, installation.Status);
        Assert.Null(installation.UpdateCheckStatus);
        Assert.Null(installation.IconColor);
    }

    [Fact]
    public void WhenADevelopmentBuildIsCreated_ThenTheVersionLinksToTheCommitAndUpdatesAreUnavailable()
    {
        // Act
        var installation = CreateInstallation(CreateBuild("1.0.0", tag: null));

        // Assert
        Assert.Equal(installation.CommitUrl, installation.VersionUrl);
        Assert.Equal("Not available for development builds", installation.UpdateCheckStatus);
    }

    [Fact]
    public void WhenTheRepositoryIsNotOnGitHub_ThenTheUpdateCheckIsNotSupported()
    {
        // Act
        var installation = CreateInstallation(CreateBuild(repositoryUrl: "https://gitlab.com/owner/repo"));

        // Assert
        Assert.Equal("Not supported for gitlab.com", installation.UpdateCheckStatus);
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
    public void WhenSubjectsAreCounted_ThenServicesInErrorAndTheInventoryAreHandled()
    {
        // Arrange
        IInterceptorSubject[] subjects =
        [
            new TestMonitoredService { Status = ServiceStatus.Running },
            new TestMonitoredService { Status = ServiceStatus.Error },
            new InstallationRepository("repo", null),
            CreateInstallation(CreateBuild()),
        ];

        // Act
        var (subjectCount, serviceCount, errorCount) = HomeBlazeInstallation.CountSubjects(subjects);

        // Assert
        Assert.Equal(3, subjectCount);
        Assert.Equal(3, serviceCount);
        Assert.Equal(1, errorCount);
    }

    [Fact]
    public void WhenPolled_ThenTheInventoryListsThisLibrary()
    {
        // Arrange
        var installation = CreateInstallation(CreateBuild());

        // Act
        installation.Poll();

        // Assert
        Assert.Contains(
            installation.Repositories.Values.SelectMany(repository => repository.Assemblies.Values),
            assembly => assembly.Name == "HomeBlaze.Services");
        Assert.NotNull(installation.LastUpdated);
    }

    [Fact]
    public void WhenTheSameInventoryIsAppliedTwice_ThenTheDictionaryAndSubjectsAreKept()
    {
        // Arrange
        var installation = CreateInstallation(CreateBuild());
        installation.ApplyInventory([CreateRepository("repo", RepositoryUrl)]);
        var repositories = installation.Repositories;
        var repository = repositories["repo"];

        // Act
        installation.ApplyInventory([CreateRepository("repo", RepositoryUrl)]);

        // Assert
        Assert.Same(repositories, installation.Repositories);
        Assert.Same(repository, installation.Repositories["repo"]);
    }

    [Fact]
    public void WhenARepositoryIsAdded_ThenANewDictionaryKeepsTheExistingSubject()
    {
        // Arrange
        var installation = CreateInstallation(CreateBuild());
        installation.ApplyInventory([CreateRepository("repo", RepositoryUrl)]);
        var repositories = installation.Repositories;
        var repository = repositories["repo"];

        // Act
        installation.ApplyInventory([CreateRepository("repo", RepositoryUrl), CreateRepository("other", "https://github.com/owner/other")]);

        // Assert
        Assert.NotSame(repositories, installation.Repositories);
        Assert.Equal(2, installation.Repositories.Count);
        Assert.Same(repository, installation.Repositories["repo"]);
    }

    [Fact]
    public void WhenARepositoryNameMovesToAnotherUrl_ThenANewSubjectWithTheNewUrlIsCreated()
    {
        // Arrange
        var installation = CreateInstallation(CreateBuild());
        installation.ApplyInventory([CreateRepository("Lib", "https://github.com/first/Lib")]);
        var repository = installation.Repositories["Lib"];

        // Act
        installation.ApplyInventory([CreateRepository("Lib", "https://github.com/second/Lib")]);

        // Assert
        Assert.NotSame(repository, installation.Repositories["Lib"]);
        Assert.Equal(new Uri("https://github.com/second/Lib"), installation.Repositories["Lib"].RepositoryUrl);
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
        var listeningUrlsProvider = new FailingOnceListeningUrlsProvider();
        var installation = CreateInstallation(CreateBuild(), listeningUrlsProvider: listeningUrlsProvider);
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
        Assert.True(listeningUrlsProvider.CallCount >= 2);
        Assert.Equal(new[] { "http://localhost:5000" }, installation.ListeningUrls);
        Assert.NotEmpty(installation.Repositories);
    }

    [Fact]
    public void WhenTheInventoryHasAMismatch_ThenTheInstallationWarns()
    {
        // Arrange
        var installation = CreateInstallation(CreateBuild());
        InventoryAssembly[] assemblies =
        [
            new("Lib", "Lib", "Default", "1.0.0", Commit, true),
            new("Lib (Plugin)", "Lib", "Plugin", "0.9.0", null, true),
        ];

        // Act
        installation.ApplyInventory([new InventoryRepository("repo", RepositoryUrl, false, [.. assemblies])]);

        // Assert
        Assert.True(installation.HasVersionMismatch);
        Assert.Equal(new[] { "repo" }, installation.MismatchedRepositories);
        Assert.Equal("Warning", installation.IconColor);
    }

    private static InventoryRepository CreateRepository(string name, string repositoryUrl) =>
        new(name, repositoryUrl, false, [new InventoryAssembly(name, name, "Default", "1.0.0", Commit, false)]);

    private sealed class FailingOnceListeningUrlsProvider : IListeningUrlsProvider
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public IReadOnlyList<string> GetListeningUrls()
        {
            if (Interlocked.Increment(ref _callCount) == 1)
            {
                throw new InvalidCastException("The first read fails");
            }

            return ["http://localhost:5000"];
        }
    }
}
