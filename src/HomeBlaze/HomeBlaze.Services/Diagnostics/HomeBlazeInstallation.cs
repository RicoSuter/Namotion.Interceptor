using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Common;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Services.Diagnostics;

/// <summary>
/// The running HomeBlaze installation: its build, environment and available updates.
/// </summary>
[Category("System")]
[Description("Reports the build, environment and available updates of this installation")]
[DisplayName("Installation")]
[SubjectIcon("Info")]
[SingleInstance]
[InterceptorSubject]
public partial class HomeBlazeInstallation : BackgroundService,
    IConfigurable,
    IMonitoredService,
    ITitleProvider,
    IIconProvider,
    ILastUpdatedProvider
{
    private readonly DeveloperModeService _developerModeService;
    private readonly GitHubReleaseClient? _releaseClient;
    private readonly string? _updateCheckUnavailableReason;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;

    [Configuration]
    public partial string Name { get; set; }

    [Configuration]
    public partial TimeSpan PollingInterval { get; set; }

    [Configuration]
    public partial bool UpdateCheckEnabled { get; set; }

    /// <summary>
    /// Time between update checks, at least 24 hours and at most 365 days.
    /// </summary>
    [Configuration]
    public partial TimeSpan UpdateCheckInterval { get; set; }

    // Build

    [State(Position = 400)]
    [Category("Build")]
    public partial string Version { get; internal set; }

    [State(IsDiscrete = true, Position = 401)]
    [Category("Build")]
    public partial InstallationChannel Channel { get; internal set; }

    [State(Position = 402)]
    [Category("Build")]
    public partial string? Commit { get; internal set; }

    [State(Position = 403)]
    [Category("Build")]
    public partial string? Tag { get; internal set; }

    [State(Position = 404)]
    [Category("Build")]
    public partial string? Branch { get; internal set; }

    [State(Position = 405)]
    [Category("Build")]
    public partial DateTimeOffset? CommitDate { get; internal set; }

    [State(Position = 406)]
    [Category("Build")]
    public partial DateTimeOffset? BuildTime { get; internal set; }

    [State(Position = 408)]
    [Category("Build")]
    public partial Uri? RepositoryUrl { get; internal set; }

    [State(Position = 409)]
    [Category("Build")]
    public partial Uri? BuildRunUrl { get; internal set; }

    [Derived]
    [State(Position = 410)]
    [Category("Build")]
    public Uri? CommitUrl =>
        RepositoryUrl is { } repositoryUrl && Commit is { } commit
            ? new Uri($"{repositoryUrl.AbsoluteUri.TrimEnd('/')}/commit/{commit}")
            : null;

    /// <summary>
    /// The release page of a release, otherwise the commit.
    /// </summary>
    [Derived]
    [State(Position = 411)]
    [Category("Build")]
    public Uri? VersionUrl =>
        Channel == InstallationChannel.Release && RepositoryUrl is { } repositoryUrl && Tag is { } tag
            ? new Uri($"{repositoryUrl.AbsoluteUri.TrimEnd('/')}/releases/tag/{Uri.EscapeDataString(tag)}")
            : CommitUrl;

    // Environment

    [State(Position = 420)]
    [Category("Environment")]
    public partial string EnvironmentName { get; internal set; }

    [State(IsDiscrete = true, Position = 421)]
    [Category("Environment")]
    public partial bool IsDeveloperMode { get; internal set; }

    [State(Position = 423)]
    [Category("Environment")]
    public partial string? DataDirectory { get; internal set; }

    [State(Unit = StateUnit.Byte, Position = 424)]
    [Category("Environment")]
    public partial long? DataVolumeFreeSpace { get; internal set; }

    [State(Unit = StateUnit.Byte, Position = 425)]
    [Category("Environment")]
    public partial long? DataVolumeTotalSize { get; internal set; }

    [Derived]
    [State(Unit = StateUnit.Percent, Position = 426)]
    [Category("Environment")]
    public decimal? DataVolumeUsage =>
        DataVolumeTotalSize is { } total and > 0 && DataVolumeFreeSpace is { } free
            ? Math.Round((decimal)(total - free) / total, 3)
            : null;

    // Update

    /// <summary>
    /// Whether a newer version exists, or null until the first successful check, and when the running version is
    /// not a semantic version.
    /// </summary>
    [State(IsDiscrete = true, Position = 460)]
    [Category("Update")]
    public partial bool? UpdateAvailable { get; internal set; }

    [State(Position = 461)]
    [Category("Update")]
    public partial string? LatestVersion { get; internal set; }

    [State(Position = 462)]
    [Category("Update")]
    public partial DateTimeOffset? LatestReleaseDate { get; internal set; }

    [State(Position = 463)]
    [Category("Update")]
    public partial Uri? LatestReleaseUrl { get; internal set; }

    [State(Position = 464)]
    [Category("Update")]
    public partial DateTimeOffset? LastUpdateCheck { get; internal set; }

    /// <summary>
    /// Why no check can run for this build, or the error of the last check until a check succeeds.
    /// </summary>
    [State(Position = 465)]
    [Category("Update")]
    public partial string? UpdateCheckStatus { get; internal set; }

    [State(IsDiscrete = true)]
    public partial ServiceStatus Status { get; internal set; }

    [State]
    public partial string? StatusMessage { get; internal set; }

    [State(Position = 950)]
    public partial DateTimeOffset? LastUpdated { get; internal set; }

    [Derived]
    public string? Title => string.IsNullOrEmpty(Name) ? "Installation" : Name;

    [Derived]
    public string IconName => "Info";

    [Derived]
    public string? IconColor => UpdateAvailable == true ? "Warning" : null;

    public HomeBlazeInstallation(
        IHostEnvironment hostEnvironment,
        IHttpClientFactory httpClientFactory,
        DeveloperModeService developerModeService,
        ILogger<HomeBlazeInstallation> logger)
        : this(
            // Never packed on its own: it is built with the application and carries the same version and metadata.
            BuildInformation.FromAssembly(typeof(HomeBlazeInstallation).Assembly),
            hostEnvironment.EnvironmentName,
            () => httpClientFactory.CreateClient(),
            developerModeService,
            TimeProvider.System,
            logger)
    {
    }

    internal HomeBlazeInstallation(
        BuildInformation build,
        string environmentName,
        Func<HttpClient> createHttpClient,
        DeveloperModeService developerModeService,
        TimeProvider timeProvider,
        ILogger logger)
    {
        _developerModeService = developerModeService;
        _timeProvider = timeProvider;
        _logger = logger;
        _releaseClient = GitHubReleaseClient.TryCreate(build.RepositoryUrl, createHttpClient, $"HomeBlaze/{build.Version}", timeProvider);

        Name = string.Empty;
        PollingInterval = TimeSpan.FromSeconds(30);
        UpdateCheckEnabled = true;
        UpdateCheckInterval = UpdateSchedule.MinimumInterval;

        Version = build.Version;
        Channel = GetChannel(build);
        Commit = build.Commit;
        Tag = build.Tag;
        Branch = build.Branch;
        CommitDate = build.CommitDate;
        BuildTime = build.BuildTime;
        RepositoryUrl = CreateUri(build.RepositoryUrl);
        BuildRunUrl = CreateUri(build.RunUrl);

        EnvironmentName = environmentName;
        IsDeveloperMode = developerModeService.IsEnabled;
        DataDirectory = null;
        DataVolumeFreeSpace = null;
        DataVolumeTotalSize = null;

        UpdateAvailable = null;
        LatestVersion = null;
        LatestReleaseDate = null;
        LatestReleaseUrl = null;
        LastUpdateCheck = null;
        _updateCheckUnavailableReason = GetUpdateCheckUnavailableReason(Channel, RepositoryUrl, _releaseClient);
        UpdateCheckStatus = _updateCheckUnavailableReason;

        Status = ServiceStatus.Stopped;
        StatusMessage = null;
        LastUpdated = null;
    }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken)
    {
        Release(_pollSignal);
        Release(_updateSignal);
        return Task.CompletedTask;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _developerModeService.OnChange += RefreshDeveloperMode;

            // Read after subscribing, so a change in between is not missed.
            RefreshDeveloperMode();

            // A request accepted while the previous run stopped would otherwise skip the delay of the first check.
            // Cleared before the status is Running, which is when CheckForUpdates accepts requests again.
            Interlocked.Exchange(ref _isManualCheckRequested, 0);
            Drain(_updateSignal);
            Drain(_pollSignal);
            Status = ServiceStatus.Running;

            await Task.WhenAll(RunPollingAsync(stoppingToken), RunUpdateChecksAsync(stoppingToken));
        }
        finally
        {
            _developerModeService.OnChange -= RefreshDeveloperMode;
            Status = ServiceStatus.Stopped;
        }
    }

    private static InstallationChannel GetChannel(BuildInformation build) =>
        build.Tag is not null ? InstallationChannel.Release :
        string.Equals(build.Version, "edge", StringComparison.OrdinalIgnoreCase) ? InstallationChannel.Edge :
        InstallationChannel.Development;

    private static Uri? CreateUri(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri : null;

    private static void Drain(SemaphoreSlim signal)
    {
        while (signal.Wait(0))
        {
        }
    }

    private static void Release(SemaphoreSlim signal)
    {
        try
        {
            signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // A wake-up is already pending.
        }
    }
}
