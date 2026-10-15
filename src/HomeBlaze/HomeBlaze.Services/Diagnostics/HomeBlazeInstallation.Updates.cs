using System.Globalization;
using System.Text.Json;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Microsoft.Extensions.Logging;
using NuGet.Versioning;

namespace HomeBlaze.Services.Diagnostics;

public partial class HomeBlazeInstallation
{
    private const string TimeoutMessage = "GitHub did not answer in time.";

    private readonly SemaphoreSlim _updateSignal = new(0, 1);
    private readonly UpdateSchedule _updateSchedule = new();

    // Set by CheckForUpdates on any thread and cleared by the update loop.
    private int _isManualCheckRequested;

    // Written by the update loop and read by CheckForUpdates on any thread; null when GitHub asked for no wait.
    private GitHubWait? _gitHubWait;

    private sealed record GitHubWait(long StartTimestamp, TimeSpan Duration);

    /// <summary>
    /// Whether a manual check or a wake-up waits for the update loop.
    /// </summary>
    internal bool HasPendingUpdateWakeUp => Volatile.Read(ref _isManualCheckRequested) == 1 || _updateSignal.CurrentCount > 0;

    [Operation(Title = "Check for updates", Description = "Asks GitHub for a newer version now.", Icon = "Update")]
    public void CheckForUpdates()
    {
        if (GetUpdateCheckUnavailableReason() is { } reason)
        {
            throw new InvalidOperationException(reason);
        }

        if (Status == ServiceStatus.Stopped)
        {
            throw new InvalidOperationException("The installation is not running.");
        }

        if (Volatile.Read(ref _gitHubWait) is { } wait)
        {
            var remaining = wait.Duration - _timeProvider.GetElapsedTime(wait.StartTimestamp);
            if (remaining > TimeSpan.Zero)
            {
                var minutes = (int)Math.Ceiling(remaining.TotalMinutes);
                throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                    $"GitHub asked to wait {minutes} more {(minutes == 1 ? "minute" : "minutes")}."));
            }
        }

        if (!_updateSchedule.TryStartManualCheck(_timeProvider.GetUtcNow()))
        {
            throw new InvalidOperationException("Updates were checked less than a minute ago.");
        }

        Interlocked.Exchange(ref _isManualCheckRequested, 1);
        Release(_updateSignal);
    }

    /// <summary>
    /// Runs one update check, writes its result and returns the delay before the next attempt after a failure, or
    /// null after a success, when the configured interval applies.
    /// </summary>
    internal async Task<TimeSpan?> CheckForUpdatesCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            var release = await _releaseClient!.GetLatestReleaseAsync(cancellationToken);

            LatestVersion = release?.Version;
            LatestReleaseDate = release?.PublishedAt;
            LatestReleaseUrl = release?.Url;
            UpdateAvailable =
                release is null ? false :
                NuGetVersion.TryParse(Version, out _) ? IsNewer(release.Version, Version) :
                null;

            LastUpdateCheck = _timeProvider.GetUtcNow();
            UpdateCheckStatus = null;

            Volatile.Write(ref _gitHubWait, null);

            _updateSchedule.ResetBackoff();
            return null;
        }
        catch (GitHubRequestException exception)
        {
            _logger.LogWarning("Checking for updates failed: {Message}", exception.Message);
            return ReportUpdateCheckFailure(exception.Message, exception.RetryAfter);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Not stopping, so the cancellation is the request timeout of the client.
            _logger.LogWarning("Checking for updates failed: {Message}", TimeoutMessage);
            return ReportUpdateCheckFailure(TimeoutMessage, retryAfter: null);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException)
        {
            _logger.LogWarning(exception, "Checking for updates failed");
            return ReportUpdateCheckFailure(exception.Message, retryAfter: null);
        }
    }

    /// <summary>
    /// Returns whether <paramref name="candidate"/> is a greater semantic version than <paramref name="current"/>,
    /// ignoring build metadata. Versions that do not parse are never newer.
    /// </summary>
    internal static bool IsNewer(string candidate, string current) =>
        NuGetVersion.TryParse(candidate, out var candidateVersion) &&
        NuGetVersion.TryParse(current, out var currentVersion) &&
        VersionComparer.VersionRelease.Compare(candidateVersion, currentVersion) > 0;

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

    private string? GetUpdateCheckUnavailableReason()
    {
        if (Channel == InstallationChannel.Development)
        {
            return "Not available for development builds";
        }

        if (Channel == InstallationChannel.Edge)
        {
            return "Not available for edge builds";
        }

        if (_releaseClient is null)
        {
            return RepositoryUrl is { } repositoryUrl
                ? $"Not supported for {repositoryUrl.Host}"
                : "Not available without a repository URL";
        }

        return null;
    }

    private async Task RunUpdateChecksAsync(CancellationToken stoppingToken)
    {
        // The due time is measured on the monotonic clock, so a wall clock that jumps does not move it.
        var lastCheckTimestamp = _timeProvider.GetTimestamp();

        // Null after a success, when the configured interval applies; the first check is delayed like a retry.
        TimeSpan? retryDelay = UpdateSchedule.GetFirstCheckDelay(Random.Shared);

        TimeSpan GetRemainingDelay() =>
            (retryDelay ?? UpdateSchedule.ClampInterval(UpdateCheckInterval)) - _timeProvider.GetElapsedTime(lastCheckTimestamp);

        while (!stoppingToken.IsCancellationRequested)
        {
            var isScheduled = UpdateCheckEnabled && GetUpdateCheckUnavailableReason() is null;
            var delay = UpdateSchedule.GetWaitDelay(isScheduled ? GetRemainingDelay() : null);
            var isSignaled = await WaitAsync(_updateSignal, delay, stoppingToken);
            if (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            // A configuration change, or a wait shortened to the hourly re-read, computes the delay again.
            var isManual = Interlocked.Exchange(ref _isManualCheckRequested, 0) == 1;
            if (!isManual &&
                (!UpdateCheckEnabled || isSignaled || GetUpdateCheckUnavailableReason() is not null || GetRemainingDelay() > TimeSpan.Zero))
            {
                continue;
            }

            lastCheckTimestamp = _timeProvider.GetTimestamp();
            try
            {
                retryDelay = await CheckForUpdatesCoreAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                // The host never observes ExecuteTask, so an escaping exception would silently end the checks.
                _logger.LogError(exception, "Checking for updates failed unexpectedly");
                retryDelay = ReportUpdateCheckFailure(exception.Message, retryAfter: null);
            }
        }
    }
}
