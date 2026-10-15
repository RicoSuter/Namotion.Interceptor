using System.ComponentModel;
using System.Reflection;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Common;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Attributes;
using DiagnosticsProcess = System.Diagnostics.Process;

namespace HomeBlaze.Services.Diagnostics;

/// <summary>
/// The running HomeBlaze installation: its build, loaded assemblies, environment, health and available updates.
/// </summary>
[Category("System")]
[Description("Reports the build, assemblies, environment, health and available updates of this installation")]
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
    private const string DefaultBranch = "master";

    private readonly BuildInformation _build;
    private readonly DeveloperModeService _developerModeService;
    private readonly IListeningUrlsProvider? _listeningUrlsProvider;
    private readonly GitHubReleaseClient? _releaseClient;
    private readonly (string Owner, string Name)? _gitHubRepository;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;

    // Guards every read-check-write of Status and StatusMessage. The message is written before the status so an
    // observer that sees Error also sees its message.
    private readonly Lock _statusLock = new();

    [Configuration]
    public partial string Name { get; set; }

    [Configuration]
    public partial TimeSpan PollingInterval { get; set; }

    [Configuration]
    public partial bool UpdateCheckEnabled { get; set; }

    /// <summary>
    /// Time between update checks, at least 24 hours.
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

    /// <summary>
    /// The branch an edge build is compared with for updates: the build branch, or master without one.
    /// </summary>
    [Derived]
    public string ComparedBranch => Branch ?? DefaultBranch;

    [State(Position = 405)]
    [Category("Build")]
    public partial DateTimeOffset? CommitDate { get; internal set; }

    [State(Position = 406)]
    [Category("Build")]
    public partial DateTimeOffset? BuildTime { get; internal set; }

    [State(Position = 407)]
    [Category("Build")]
    public partial string? ImageTags { get; internal set; }

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

    [State(Position = 422)]
    [Category("Environment")]
    public partial string[] ListeningUrls { get; internal set; }

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

    [State(Position = 427)]
    [Category("Environment")]
    public partial DateTimeOffset StartTime { get; internal set; }

    // Health

    /// <summary>
    /// Subjects in the graph, without the repositories and assemblies of this installation.
    /// </summary>
    [State(Position = 440)]
    public partial int SubjectCount { get; internal set; }

    [State(Position = 441)]
    public partial int MonitoredServiceCount { get; internal set; }

    [State(Position = 442)]
    public partial int ServicesInErrorCount { get; internal set; }

    // Update

    /// <summary>
    /// Whether a newer version exists, or null until the first successful check.
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
    /// Commits the branch of an edge build has that the running build does not.
    /// </summary>
    [State(Position = 464)]
    [Category("Update")]
    public partial int? CommitsBehind { get; internal set; }

    [State(Position = 465)]
    [Category("Update")]
    public partial Uri? ChangesUrl { get; internal set; }

    [State(Position = 466)]
    [Category("Update")]
    public partial DateTimeOffset? LastUpdateCheck { get; internal set; }

    /// <summary>
    /// Why no check runs, or the error of the last check.
    /// </summary>
    [State(Position = 467)]
    [Category("Update")]
    public partial string? UpdateCheckStatus { get; internal set; }

    // Inventory

    [State(IsDiscrete = true, Position = 480)]
    public partial bool HasVersionMismatch { get; internal set; }

    [State(Position = 481)]
    public partial string[] MismatchedRepositories { get; internal set; }

    [State(Position = 490)]
    public partial Dictionary<string, InstallationRepository> Repositories { get; internal set; }

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
    public string? IconColor =>
        Status == ServiceStatus.Error ? "Error" :
        UpdateAvailable == true || HasVersionMismatch ? "Warning" :
        null;

    public HomeBlazeInstallation(
        IHostEnvironment hostEnvironment,
        IHttpClientFactory httpClientFactory,
        DeveloperModeService developerModeService,
        ILogger<HomeBlazeInstallation> logger,
        IListeningUrlsProvider? listeningUrlsProvider = null)
        : this(
            BuildInformation.FromAssembly(FindApplicationAssembly(hostEnvironment.ApplicationName)),
            hostEnvironment.EnvironmentName,
            () => httpClientFactory.CreateClient(),
            developerModeService,
            listeningUrlsProvider,
            TimeProvider.System,
            logger)
    {
    }

    internal HomeBlazeInstallation(
        BuildInformation build,
        string environmentName,
        Func<HttpClient>? createHttpClient,
        DeveloperModeService developerModeService,
        IListeningUrlsProvider? listeningUrlsProvider,
        TimeProvider timeProvider,
        ILogger logger)
    {
        _build = build;
        _developerModeService = developerModeService;
        _listeningUrlsProvider = listeningUrlsProvider;
        _timeProvider = timeProvider;
        _logger = logger;
        _gitHubRepository = GitHubReleaseClient.TryParseRepository(build.RepositoryUrl);
        _releaseClient = createHttpClient is not null && _gitHubRepository is not null
            ? new GitHubReleaseClient(createHttpClient, $"HomeBlaze/{build.Version}", timeProvider)
            : null;

        Name = string.Empty;
        PollingInterval = TimeSpan.FromSeconds(30);
        UpdateCheckEnabled = false;
        UpdateCheckInterval = UpdateSchedule.MinimumInterval;

        Version = build.Version;
        Channel = GetChannel(build);
        Commit = build.Commit;
        Tag = build.Tag;
        Branch = build.Branch;
        CommitDate = build.CommitDate;
        BuildTime = build.BuildTime;
        ImageTags = build.ImageTags;
        RepositoryUrl = CreateUri(build.RepositoryUrl);
        BuildRunUrl = CreateUri(build.RunUrl);

        EnvironmentName = environmentName;
        IsDeveloperMode = developerModeService.IsEnabled;
        ListeningUrls = [];
        DataDirectory = null;
        DataVolumeFreeSpace = null;
        DataVolumeTotalSize = null;
        StartTime = ReadStartTime();

        SubjectCount = 0;
        MonitoredServiceCount = 0;
        ServicesInErrorCount = 0;

        UpdateAvailable = null;
        LatestVersion = null;
        LatestReleaseDate = null;
        LatestReleaseUrl = null;
        CommitsBehind = null;
        ChangesUrl = null;
        LastUpdateCheck = null;
        UpdateCheckStatus = GetUpdateCheckUnavailableReason();

        HasVersionMismatch = false;
        MismatchedRepositories = [];
        Repositories = new Dictionary<string, InstallationRepository>(StringComparer.Ordinal);

        Status = ServiceStatus.Stopped;
        StatusMessage = null;
        LastUpdated = null;
    }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken)
    {
        ClearUpdateCheckErrorIfDisabled();
        Release(_pollSignal);
        Release(_updateSignal);
        return Task.CompletedTask;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
            _developerModeService.OnChange += OnDeveloperModeChanged;
            Interlocked.Exchange(ref _isInventoryOutdated, 1);

            // The handler may run between the read and the write, so a second read repairs a stale write.
            var isDeveloperMode = _developerModeService.IsEnabled;
            IsDeveloperMode = isDeveloperMode;
            if (isDeveloperMode != _developerModeService.IsEnabled)
            {
                IsDeveloperMode = _developerModeService.IsEnabled;
            }

            SetStatus(ServiceStatus.Running, null);

            await Task.WhenAll(RunPollingAsync(stoppingToken), RunUpdateChecksAsync(stoppingToken));
        }
        finally
        {
            AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;
            _developerModeService.OnChange -= OnDeveloperModeChanged;
            SetStatus(ServiceStatus.Stopped, null);
        }
    }

    private void SetStatus(ServiceStatus status, string? message)
    {
        lock (_statusLock)
        {
            StatusMessage = message;
            Status = status;
        }
    }

    /// <summary>
    /// Clears an update check error when the check is disabled and returns whether it is disabled.
    /// </summary>
    private bool ClearUpdateCheckErrorIfDisabled()
    {
        lock (_statusLock)
        {
            if (UpdateCheckEnabled)
            {
                return false;
            }

            ClearUpdateCheckError();
            return true;
        }
    }

    private void ClearUpdateCheckError()
    {
        lock (_statusLock)
        {
            if (Status == ServiceStatus.Error)
            {
                StatusMessage = null;
                Status = ServiceStatus.Running;
            }
        }
    }

    internal static InstallationChannel GetChannel(BuildInformation build) =>
        build.Tag is not null ? InstallationChannel.Release :
        string.Equals(build.Version, "edge", StringComparison.OrdinalIgnoreCase) ? InstallationChannel.Edge :
        InstallationChannel.Development;

    // Under a test host the entry assembly is the test runner, so the application is found by its name.
    private static Assembly FindApplicationAssembly(string applicationName)
    {
        var entryAssembly = Assembly.GetEntryAssembly();
        if (entryAssembly is not null && string.Equals(entryAssembly.GetName().Name, applicationName, StringComparison.Ordinal))
        {
            return entryAssembly;
        }

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (string.Equals(assembly.GetName().Name, applicationName, StringComparison.Ordinal))
            {
                return assembly;
            }
        }

        return entryAssembly ?? typeof(HomeBlazeInstallation).Assembly;
    }

    private static DateTimeOffset ReadStartTime()
    {
        try
        {
            using var process = DiagnosticsProcess.GetCurrentProcess();
            return new DateTimeOffset(process.StartTime);
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
        {
            return DateTimeOffset.UtcNow;
        }
    }

    private static Uri? CreateUri(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri : null;

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
