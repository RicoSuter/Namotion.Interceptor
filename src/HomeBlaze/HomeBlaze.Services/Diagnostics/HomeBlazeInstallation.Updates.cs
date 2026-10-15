using System.Text.Json;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Microsoft.Extensions.Logging;

namespace HomeBlaze.Services.Diagnostics;

public partial class HomeBlazeInstallation
{
    private const string TimeoutMessage = "GitHub did not answer in time.";

    private readonly SemaphoreSlim _updateSignal = new(0, 1);
    private readonly UpdateSchedule _updateSchedule = new();

    // Set by CheckForUpdates on any thread and cleared by the update loop.
    private int _isManualCheckRequested;

    [Operation(Title = "Check for updates", Description = "Asks GitHub for a newer version now.", Icon = "Update")]
    public void CheckForUpdates()
    {
        if (GetUpdateCheckUnavailableReason() is { } reason)
        {
            throw new InvalidOperationException(reason);
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
        var (owner, name) = _gitHubRepository!.Value;
        try
        {
            var release = await _releaseClient!.GetLatestReleaseAsync(owner, name, cancellationToken);
            var comparison = Channel == InstallationChannel.Edge && Commit is { } commit
                ? await _releaseClient.CompareWithBranchAsync(owner, name, commit, Branch ?? "master", cancellationToken)
                : null;
            var state = UpdateEvaluator.Evaluate(Channel, Version, release, comparison);

            LatestVersion = state.LatestVersion;
            LatestReleaseDate = state.LatestReleaseDate;
            LatestReleaseUrl = state.LatestReleaseUrl;
            CommitsBehind = state.CommitsBehind;
            ChangesUrl = state.ChangesUrl;
            UpdateAvailable = state.UpdateAvailable;

            LastUpdateCheck = _timeProvider.GetUtcNow();
            UpdateCheckStatus = null;
            ClearUpdateCheckError();

            // Only resets the backoff; the loop reads the interval on every wake so a changed interval applies at once.
            _updateSchedule.GetDelayAfterSuccess(UpdateCheckInterval);
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

    private TimeSpan ReportUpdateCheckFailure(string message, TimeSpan? retryAfter)
    {
        LastUpdateCheck = _timeProvider.GetUtcNow();
        UpdateCheckStatus = message;
        lock (_statusLock)
        {
            if (UpdateCheckEnabled)
            {
                StatusMessage = message;
                Status = ServiceStatus.Error;
            }
        }

        return _updateSchedule.GetDelayAfterFailure(retryAfter);
    }

    private string? GetUpdateCheckUnavailableReason()
    {
        if (Channel == InstallationChannel.Development)
        {
            return "Not available for development builds";
        }

        if (_gitHubRepository is null)
        {
            return RepositoryUrl is { } repositoryUrl
                ? $"Not supported for {repositoryUrl.Host}"
                : "Not available without a repository URL";
        }

        if (Channel == InstallationChannel.Edge && Commit is null)
        {
            return "Not available without a commit";
        }

        return _releaseClient is null ? "Not available without an HTTP client" : null;
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
            var delay = Timeout.InfiniteTimeSpan;
            if (UpdateCheckEnabled && GetUpdateCheckUnavailableReason() is null)
            {
                var remaining = GetRemainingDelay();
                delay = remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
            }

            var isSignaled = await WaitAsync(_updateSignal, delay, stoppingToken);
            if (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            var isManual = Interlocked.Exchange(ref _isManualCheckRequested, 0) == 1;
            if (!isManual)
            {
                if (!UpdateCheckEnabled)
                {
                    ClearUpdateCheckError();
                    continue;
                }

                // A configuration change, or a wait capped below a long interval, computes the delay again.
                if (isSignaled || GetRemainingDelay() > TimeSpan.Zero)
                {
                    continue;
                }
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
