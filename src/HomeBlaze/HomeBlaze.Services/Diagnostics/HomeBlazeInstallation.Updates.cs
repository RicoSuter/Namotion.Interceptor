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
    /// Runs one update check, writes its result and returns when the next scheduled check is due.
    /// </summary>
    internal async Task<DateTimeOffset> CheckForUpdatesCoreAsync(CancellationToken cancellationToken)
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

            var now = _timeProvider.GetUtcNow();
            LastUpdateCheck = now;
            UpdateCheckStatus = null;
            if (Status == ServiceStatus.Error)
            {
                StatusMessage = null;
                Status = ServiceStatus.Running;
            }

            return now + _updateSchedule.GetDelayAfterSuccess(UpdateCheckInterval);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or GitHubRequestException or JsonException ||
            (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // Not stopping, so the cancellation is the request timeout of the client.
            var message = exception is OperationCanceledException ? TimeoutMessage : exception.Message;
            return ReportUpdateCheckFailure(exception, message, (exception as GitHubRequestException)?.RetryAfter);
        }
    }

    private DateTimeOffset ReportUpdateCheckFailure(Exception exception, string message, TimeSpan? retryAfter)
    {
        _logger.LogWarning(exception, "Checking for updates failed");

        var now = _timeProvider.GetUtcNow();
        LastUpdateCheck = now;
        UpdateCheckStatus = message;
        if (UpdateCheckEnabled)
        {
            // The message is written first so an observer that sees Error also sees its message.
            StatusMessage = message;
            Status = ServiceStatus.Error;
        }

        return now + _updateSchedule.GetDelayAfterFailure(retryAfter);
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
        var nextCheckAt = _timeProvider.GetUtcNow() + UpdateSchedule.GetFirstCheckDelay(Random.Shared);
        while (!stoppingToken.IsCancellationRequested)
        {
            var isScheduled = UpdateCheckEnabled && GetUpdateCheckUnavailableReason() is null;
            var remaining = nextCheckAt - _timeProvider.GetUtcNow();
            var delay = !isScheduled ? Timeout.InfiniteTimeSpan : remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;

            var isSignaled = await WaitAsync(_updateSignal, delay, stoppingToken);
            if (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            var isManual = Interlocked.Exchange(ref _isManualCheckRequested, 0) == 1;
            if (isSignaled && !isManual)
            {
                // The configuration changed; the delay is computed again.
                continue;
            }

            if (isManual || UpdateCheckEnabled)
            {
                try
                {
                    nextCheckAt = await CheckForUpdatesCoreAsync(stoppingToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    // The host never observes ExecuteTask, so an escaping exception would silently end the checks.
                    nextCheckAt = ReportUpdateCheckFailure(exception, exception.Message, retryAfter: null);
                }
            }
        }
    }
}
