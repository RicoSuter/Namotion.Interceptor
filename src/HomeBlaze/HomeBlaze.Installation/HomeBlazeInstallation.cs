using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Common;
using HomeBlaze.Services;
using HomeBlaze.Services.Clients;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Attributes;
using NuGet.Versioning;

namespace HomeBlaze.Installation;

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
    private readonly ClientConnectionService _clientConnectionService;
    private readonly GitHubRepositoryClient? _gitHubClient;
    private readonly string? _updateCheckUnavailableReason;

    // Null when the running version is not a semantic version, which no release can be compared with.
    private readonly NuGetVersion? _runningVersion;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly Lock _developerModeLock = new();

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

    [State(Position = 410)]
    [Category("Build")]
    public partial Uri? CommitUrl { get; internal set; }

    /// <summary>
    /// The release page of a release, otherwise the commit.
    /// </summary>
    [State(Position = 411)]
    [Category("Build")]
    public partial Uri? VersionUrl { get; internal set; }

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

    // Runtime

    [State(Position = 441)]
    [Category("Runtime")]
    public partial int? SubjectCount { get; internal set; }

    /// <summary>
    /// All registered properties, including attributes, method holders and subject references.
    /// </summary>
    [State(Position = 442)]
    [Category("Runtime")]
    public partial int? PropertyCount { get; internal set; }

    [State(Position = 443)]
    [Category("Runtime")]
    public partial int? StatePropertyCount { get; internal set; }

    [State(Position = 444)]
    [Category("Runtime")]
    public partial int? ConfigurationPropertyCount { get; internal set; }

    [State(Position = 445)]
    [Category("Runtime")]
    public partial int? OperationCount { get; internal set; }

    [State(Position = 446)]
    [Category("Runtime")]
    public partial int? QueryCount { get; internal set; }

    [State(Position = 447)]
    [Category("Runtime")]
    public partial int? NotificationPublisherCount { get; internal set; }

    // Update

    /// <summary>
    /// Whether a newer release exists or, for an edge build, whether its branch has newer commits; null until the first
    /// successful check, when the running version of a release build is not a semantic version, and when GitHub cannot
    /// compare the commit of an edge build with its branch.
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

    /// <summary>
    /// The number of commits of its branch that an edge build lacks; null for other builds, until the first successful
    /// check, and when GitHub cannot compare the commit with the branch.
    /// </summary>
    [State(Position = 464)]
    [Category("Update")]
    public partial int? CommitsBehind { get; internal set; }

    /// <summary>
    /// The GitHub page that lists the commits of the branch that an edge build lacks; null whenever
    /// <see cref="CommitsBehind"/> is null.
    /// </summary>
    [State(Position = 465)]
    [Category("Update")]
    public partial Uri? CompareUrl { get; internal set; }

    [State(Position = 466)]
    [Category("Update")]
    public partial DateTimeOffset? LastUpdateCheck { get; internal set; }

    /// <summary>
    /// Why no check can run for this build, the error of the last check until a check succeeds, or why the last check
    /// could not place the commit of an edge build on its branch.
    /// </summary>
    [State(Position = 467)]
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
        ClientConnectionService clientConnectionService,
        ILogger<HomeBlazeInstallation> logger)
        : this(
            // Never packed on its own: it is built with the application and carries the same version and metadata.
            BuildInformation.FromAssembly(typeof(HomeBlazeInstallation).Assembly),
            hostEnvironment.EnvironmentName,
            () => httpClientFactory.CreateClient(),
            developerModeService,
            clientConnectionService,
            TimeProvider.System,
            logger)
    {
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("SonarAnalyzer", "S107", Justification = "The internal constructor takes the dependencies of the public one plus the clock and HTTP seams the tests replace.")]
    internal HomeBlazeInstallation(
        BuildInformation build,
        string environmentName,
        Func<HttpClient> createHttpClient,
        DeveloperModeService developerModeService,
        ClientConnectionService clientConnectionService,
        TimeProvider timeProvider,
        ILogger logger,
        TimeSpan? requestTimeout = null)
    {
        _developerModeService = developerModeService;
        _clientConnectionService = clientConnectionService;
        _timeProvider = timeProvider;
        _logger = logger;
        _gitHubClient = GitHubRepositoryClient.TryCreate(build.RepositoryUrl, createHttpClient, $"HomeBlaze/{build.Version}", timeProvider, requestTimeout);

        Name = string.Empty;
        PollingInterval = TimeSpan.FromSeconds(30);
        UpdateCheckEnabled = true;
        UpdateCheckInterval = UpdateSchedule.MinimumInterval;
        Clients = new Dictionary<string, HomeBlazeClient>();

        Version = build.Version;
        _runningVersion = NuGetVersion.TryParse(build.Version, out var runningVersion) ? runningVersion : null;
        Channel = GetChannel(build);
        Commit = build.Commit;
        Tag = build.Tag;
        Branch = build.Branch;
        CommitDate = build.CommitDate;
        BuildTime = build.BuildTime;
        RepositoryUrl = CreateUri(build.RepositoryUrl);
        BuildRunUrl = CreateUri(build.RunUrl);

        var repositoryAddress = RepositoryUrl?.AbsoluteUri.TrimEnd('/');
        CommitUrl = repositoryAddress is not null && build.Commit is { } commit
            ? new Uri($"{repositoryAddress}/commit/{commit}")
            : null;
        VersionUrl = repositoryAddress is not null && build.Tag is { } tag
            ? new Uri($"{repositoryAddress}/releases/tag/{Uri.EscapeDataString(tag)}")
            : CommitUrl;

        EnvironmentName = environmentName;
        IsDeveloperMode = developerModeService.IsEnabled;
        DataDirectory = null;
        DataVolumeFreeSpace = null;
        DataVolumeTotalSize = null;
        SubjectCount = null;
        PropertyCount = null;
        StatePropertyCount = null;
        ConfigurationPropertyCount = null;
        OperationCount = null;
        QueryCount = null;
        NotificationPublisherCount = null;
        ConnectedCircuitCount = 0;

        UpdateAvailable = null;
        LatestVersion = null;
        LatestReleaseDate = null;
        LatestReleaseUrl = null;
        CommitsBehind = null;
        CompareUrl = null;
        LastUpdateCheck = null;
        _updateCheckUnavailableReason = GetUpdateCheckUnavailableReason(Channel, build);
        UpdateCheckStatus = _updateCheckUnavailableReason;

        Status = ServiceStatus.Stopped;
        StatusMessage = null;
        LastUpdated = null;
    }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken)
    {
        ReconcileClients();
        Release(_pollSignal);
        Release(_updateSignal);
        return Task.CompletedTask;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        IDisposable? renameRegistration = null;
        try
        {
            _developerModeService.OnChange += RefreshDeveloperMode;
            _clientConnectionService.Changed += OnClientConnectionsChanged;
            renameRegistration = _clientConnectionService.SetRenameHandler(RenameConnectionAsync);

            // Read after subscribing, so a change in between is not missed.
            RefreshDeveloperMode();
            ReconcileClients();
            Status = ServiceStatus.Running;

            await Task.WhenAll(RunPollingAsync(stoppingToken), RunUpdateChecksAsync(stoppingToken));
        }
        finally
        {
            _developerModeService.OnChange -= RefreshDeveloperMode;
            _clientConnectionService.Changed -= OnClientConnectionsChanged;
            renameRegistration?.Dispose();
            Status = ServiceStatus.Stopped;
        }
    }

    private static InstallationChannel GetChannel(BuildInformation build)
    {
        if (build.Tag is not null)
        {
            return InstallationChannel.Release;
        }

        return string.Equals(build.Version, "edge", StringComparison.OrdinalIgnoreCase)
            ? InstallationChannel.Edge
            : InstallationChannel.Development;
    }

    private static Uri? CreateUri(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri : null;

    private void RefreshDeveloperMode()
    {
        // Reading and writing under one lock keeps a change event that races the start from being overwritten.
        lock (_developerModeLock)
        {
            IsDeveloperMode = _developerModeService.IsEnabled;
        }
    }

    /// <summary>
    /// Waits for the signal or the delay, at most <see cref="int.MaxValue"/> milliseconds, and returns whether the
    /// signal woke it; false also when stopping.
    /// </summary>
    private static async Task<bool> WaitAsync(SemaphoreSlim signal, TimeSpan delay, CancellationToken stoppingToken)
    {
        try
        {
            return await signal.WaitAsync(delay, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return false;
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
