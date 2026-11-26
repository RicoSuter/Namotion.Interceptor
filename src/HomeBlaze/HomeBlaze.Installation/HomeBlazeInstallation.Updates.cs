using System.Globalization;
using System.Text.Json;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Microsoft.Extensions.Logging;
using NuGet.Versioning;

namespace HomeBlaze.Installation;

public partial class HomeBlazeInstallation
{
    private const string TimeoutMessage = "GitHub did not answer in time.";

    // CI publishes the image of a release about 15 minutes after the release; offering it earlier ends as UpToDate.
    private const int ReleaseImageDelayMinutes = 30;

    private readonly SemaphoreSlim _updateSignal = new(0, 1);
    private readonly UpdateSchedule _updateSchedule = new();

    // Set by CheckForUpdates on any thread and cleared by the update loop.
    private int _isManualCheckRequested;

    // Written by the update loop and read by CheckForUpdates on any thread; null when GitHub asked for no wait.
    private GitHubWait? _gitHubWait;

    private sealed record GitHubWait(long StartTimestamp, TimeSpan Duration);

    [Operation(Title = "Check for updates", Description = "Asks GitHub for a newer version now.", Icon = "Update")]
    public void CheckForUpdates()
    {
        if (_updateCheckUnavailableReason is { } reason)
        {
            throw new InvalidOperationException(reason);
        }

        if (Status == ServiceStatus.Stopped)
        {
            throw new InvalidOperationException("The installation is not running.");
        }

        var remainingWait = GetRemainingGitHubWait();
        if (remainingWait > TimeSpan.Zero)
        {
            var minutes = (int)Math.Ceiling(remainingWait.TotalMinutes);
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                $"GitHub asked to wait {minutes} more {(minutes == 1 ? "minute" : "minutes")}."));
        }

        if (!_updateSchedule.TryStartManualCheck(_timeProvider))
        {
            throw new InvalidOperationException("Updates were checked less than a minute ago.");
        }

        Volatile.Write(ref _isManualCheckRequested, 1);
        Release(_updateSignal);
    }

    /// <summary>
    /// Runs one update check, writes its result and returns the delay before the next attempt after a failure or while a
    /// release is held back, or null after another success, when the configured interval applies.
    /// </summary>
    internal async Task<TimeSpan?> CheckForUpdatesCoreAsync(CancellationToken cancellationToken)
    {
        var gitHubClient = _gitHubClient ?? throw new InvalidOperationException(_updateCheckUnavailableReason);
        try
        {
            var (status, nextCheckDelay) = Channel == InstallationChannel.Edge
                ? (await CompareWithBranchAsync(gitHubClient, cancellationToken), null)
                : await CompareWithLatestReleaseAsync(gitHubClient, cancellationToken);

            LastUpdateCheck = _timeProvider.GetUtcNow();
            UpdateCheckStatus = status;

            Volatile.Write(ref _gitHubWait, null);

            _updateSchedule.ResetBackoff();
            return nextCheckDelay;
        }
        catch (GitHubRequestException exception)
        {
            _logger.LogWarning(exception, "Checking for updates failed: {Message}", exception.Message);
            return ReportUpdateCheckFailure(exception.Message, exception.RetryAfter);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Not stopping, so the cancellation is the request timeout of the client.
            _logger.LogWarning(exception, "Checking for updates failed: {Message}", TimeoutMessage);
            return ReportUpdateCheckFailure(TimeoutMessage, retryAfter: null);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException)
        {
            _logger.LogWarning(exception, "Checking for updates failed: {Message}", exception.Message);
            return ReportUpdateCheckFailure(exception.Message, retryAfter: null);
        }
    }

    /// <summary>
    /// Writes the latest release and whether it is newer than the running version. A newer release published less than
    /// <see cref="ReleaseImageDelayMinutes"/> ago is not written yet, so the release found before stays offered; then
    /// it returns a status saying so and the time until the new release is due.
    /// </summary>
    private async Task<(string? Status, TimeSpan? NextCheckDelay)> CompareWithLatestReleaseAsync(GitHubRepositoryClient gitHubClient, CancellationToken cancellationToken)
    {
        var release = await gitHubClient.GetLatestReleaseAsync(cancellationToken);
        var isNewer = _runningVersion is not null && release is not null && IsNewer(release.Version, _runningVersion);
        var imageWait = isNewer ? GetReleaseImageWait(release!) : TimeSpan.Zero;
        if (imageWait > TimeSpan.Zero)
        {
            UpdateAvailable ??= false;
            return ($"Release {release!.Version} is offered {ReleaseImageDelayMinutes} minutes after it was published, once its image exists.", imageWait);
        }

        LatestVersion = release?.Version;
        LatestReleaseDate = release?.PublishedAt;
        LatestReleaseUrl = release?.Url;
        UpdateAvailable = _runningVersion is null ? null : isNewer;
        return (null, null);
    }

    // Measured on GitHub's clock, so a local clock that is off neither holds a release forever nor offers it early.
    private static TimeSpan GetReleaseImageWait(LatestRelease release) =>
        release is { PublishedAt: { } publishedAt, AnsweredAt: { } answeredAt }
            ? publishedAt.AddMinutes(ReleaseImageDelayMinutes) - answeredAt
            : TimeSpan.Zero;

    /// <summary>
    /// Writes how many commits the branch of the edge build has that its commit lacks, and returns why the commit
    /// could not be placed on the branch, or null when it is on the branch.
    /// </summary>
    private async Task<string?> CompareWithBranchAsync(GitHubRepositoryClient gitHubClient, CancellationToken cancellationToken)
    {
        // An edge build that lacks either has an unavailable reason and never checks.
        var commit = Commit ?? throw new InvalidOperationException("The build has no commit.");
        var branch = Branch ?? throw new InvalidOperationException("The build has no branch.");
        var comparison = await gitHubClient.CompareWithBranchAsync(commit, branch, cancellationToken);

        CommitsBehind = comparison?.CommitsBehind;
        CompareUrl = comparison?.Url;
        UpdateAvailable = comparison is null ? null : comparison.CommitsBehind > 0;

        var shortCommit = commit.Length > 7 ? commit[..7] : commit;
        if (comparison is null)
        {
            return $"GitHub could not compare commit {shortCommit} with {branch}.";
        }

        return comparison.CommitsAhead > 0 ? $"Commit {shortCommit} is not on {branch}." : null;
    }

    /// <summary>
    /// Returns whether <paramref name="candidate"/> is a greater semantic version than <paramref name="current"/>,
    /// ignoring build metadata. A candidate that does not parse is never newer.
    /// </summary>
    internal static bool IsNewer(string candidate, NuGetVersion current) =>
        NuGetVersion.TryParse(candidate, out var candidateVersion) &&
        VersionComparer.VersionRelease.Compare(candidateVersion, current) > 0;

    // A request accepted while a check was in flight would otherwise bypass a wait that check recorded.
    /// <summary>
    /// Clears a pending manual check request and returns whether it may run, which it may not while GitHub asked to
    /// wait. Only the update loop calls it.
    /// </summary>
    internal bool TryTakeManualCheckRequest() =>
        Interlocked.Exchange(ref _isManualCheckRequested, 0) == 1 && GetRemainingGitHubWait() <= TimeSpan.Zero;

    private TimeSpan GetRemainingGitHubWait() =>
        Volatile.Read(ref _gitHubWait) is { } wait
            ? wait.Duration - _timeProvider.GetElapsedTime(wait.StartTimestamp)
            : TimeSpan.Zero;

    private TimeSpan ReportUpdateCheckFailure(string message, TimeSpan? retryAfter)
    {
        LastUpdateCheck = _timeProvider.GetUtcNow();
        UpdateCheckStatus = message;
        if (retryAfter is { } requested)
        {
            Volatile.Write(ref _gitHubWait, new GitHubWait(_timeProvider.GetTimestamp(), UpdateSchedule.CapRetryAfter(requested)));
        }

        return _updateSchedule.GetDelayAfterFailure(retryAfter);
    }

    private static string? GetUpdateCheckUnavailableReason(InstallationChannel channel, BuildInformation build) =>
        channel switch
        {
            InstallationChannel.Development => "Not available for development builds",
            InstallationChannel.Edge when build.Commit is null || build.Branch is null => "Not available for edge builds without a commit or branch",
            _ => GitHubRepositoryClient.GetUnsupportedReason(build.RepositoryUrl)
        };

    private async Task RunUpdateChecksAsync(CancellationToken stoppingToken)
    {
        if (_updateCheckUnavailableReason is not null)
        {
            return;
        }

        // The due time is measured on the monotonic clock, so a wall clock that jumps does not move it.
        var lastCheckTimestamp = _timeProvider.GetTimestamp();

        // Null after a success, when the configured interval applies; the first check is delayed like a retry.
        TimeSpan? retryDelay = UpdateSchedule.GetFirstCheckDelay(Random.Shared);

        TimeSpan GetRemainingDelay() =>
            (retryDelay ?? UpdateSchedule.ClampInterval(UpdateCheckInterval)) - _timeProvider.GetElapsedTime(lastCheckTimestamp);

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = UpdateSchedule.GetWaitDelay(UpdateCheckEnabled ? GetRemainingDelay() : null);
            var isSignaled = await WaitAsync(_updateSignal, delay, stoppingToken);
            if (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            // A configuration change, or a wait shortened to the hourly re-read, computes the delay again.
            var isManual = TryTakeManualCheckRequest();
            if (!isManual && (!UpdateCheckEnabled || isSignaled || GetRemainingDelay() > TimeSpan.Zero))
            {
                continue;
            }

            lastCheckTimestamp = _timeProvider.GetTimestamp();
            retryDelay = await RunUpdateCheckAsync(stoppingToken);
        }
    }

    /// <summary>
    /// Runs <see cref="CheckForUpdatesCoreAsync"/> and reports an unexpected exception as a failed check; only a
    /// cancellation while stopping escapes.
    /// </summary>
    private async Task<TimeSpan?> RunUpdateCheckAsync(CancellationToken stoppingToken)
    {
        try
        {
            return await CheckForUpdatesCoreAsync(stoppingToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            // The host never observes ExecuteTask, so an escaping exception would silently end the checks.
            _logger.LogError(exception, "Checking for updates failed unexpectedly");
            return ReportUpdateCheckFailure(exception.Message, retryAfter: null);
        }
    }
}
