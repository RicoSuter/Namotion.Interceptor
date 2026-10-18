using HomeBlaze.Abstractions;
using Microsoft.Extensions.Logging;

namespace Namotion.Devices.Pushover;

public partial class PushoverService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var token = ApplicationToken;
            if (string.IsNullOrEmpty(token))
            {
                StatusMessage = "No application token configured";
                Status = ServiceStatus.Stopped;
                await WaitForSignalAsync(Timeout.InfiniteTimeSpan, stoppingToken);
                continue;
            }

            try
            {
                if (_isValidationRequested)
                {
                    // Cleared before validating, so a change during validation requests another one.
                    _isValidationRequested = false;
                    Status = ServiceStatus.Starting;
                    StatusMessage = "Validating targets...";
                    await ValidateTargetsAsync(token, stoppingToken);
                }

                StatusMessage = null;
                Status = ServiceStatus.Running;

                var hasActiveEmergencies = await PollReceiptsAsync(token, stoppingToken);
                var nextRemovalAt = RemoveFinishedEmergencies();
                await WaitForSignalAsync(GetWaitTimeout(hasActiveEmergencies, nextRemovalAt), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (PushoverException exception) when (exception.IsApplicationTokenInvalid)
            {
                _logger.LogError(exception, "Pushover rejected the application token");
                _isValidationRequested = true;
                StatusMessage = "The application token is invalid";
                Status = ServiceStatus.Error;

                // Retrying cannot help until the token changes, which signals the loop.
                await WaitForSignalAsync(Timeout.InfiniteTimeSpan, stoppingToken);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Pushover service failed");
                _isValidationRequested = true;
                StatusMessage = exception.Message;
                Status = ServiceStatus.Error;
                await WaitForSignalAsync(RetryInterval, stoppingToken);
            }
        }

        StatusMessage = null;
        Status = ServiceStatus.Stopped;
    }

    private async Task ValidateTargetsAsync(string token, CancellationToken cancellationToken)
    {
        // Targets is replaced, never changed in place, so the enumerated instance stays the same.
        foreach (var target in Targets.Values)
        {
            var userKey = target.UserKey;
            if (string.IsNullOrEmpty(userKey))
            {
                target.SetValidationFailed("No user key configured");
                continue;
            }

            try
            {
                var response = await _client.ValidateUserAsync(token, userKey, cancellationToken);
                target.SetValidated(response.Devices ?? []);
            }
            catch (PushoverException exception) when (!exception.IsApplicationTokenInvalid)
            {
                // 4xx except 429 cannot succeed on retry; anything else is rethrown to retry with validation requested.
                if (!IsPermanentValidationFailure(exception))
                {
                    throw;
                }

                _logger.LogWarning(exception, "Pushover target {Target} failed validation", target.Title);
                target.SetValidationFailed(exception.Message);
            }
        }
    }

    private static bool IsPermanentValidationFailure(PushoverException exception) =>
        exception.IsUserKeyInvalid ||
        (exception.StatusCode is { } statusCode && (int)statusCode is >= 400 and < 500 and not 429);

    private async Task<bool> PollReceiptsAsync(string token, CancellationToken cancellationToken)
    {
        var hasActiveEmergencies = false;
        var now = DateTimeOffset.UtcNow;
        foreach (var target in Targets.Values)
        {
            foreach (var receipt in target.StartReceiptPolls(now, MinimumReceiptPollingInterval))
            {
                try
                {
                    var response = await _client.GetReceiptAsync(token, receipt, cancellationToken);
                    target.ApplyReceipt(receipt, response);
                }
                catch (PushoverException exception)
                {
                    _logger.LogWarning(exception, "Reading an emergency receipt of Pushover target {Target} failed", target.Title);
                    target.ApplyReceiptError(receipt, DateTimeOffset.UtcNow);
                }
            }

            hasActiveEmergencies |= target.HasActiveEmergencies;
        }

        return hasActiveEmergencies;
    }

    private DateTimeOffset? RemoveFinishedEmergencies()
    {
        DateTimeOffset? nextRemovalAt = null;
        var now = DateTimeOffset.UtcNow;
        foreach (var target in Targets.Values)
        {
            if (target.RemoveFinishedEmergencies(now, FinishedEmergencyRetention) is { } removalAt &&
                (nextRemovalAt is null || removalAt < nextRemovalAt))
            {
                nextRemovalAt = removalAt;
            }
        }

        return nextRemovalAt;
    }

    // The earlier of the next receipt poll and the next removal of a finished emergency; with neither pending, the
    // loop waits for a signal only.
    private TimeSpan GetWaitTimeout(bool hasActiveEmergencies, DateTimeOffset? nextRemovalAt)
    {
        var timeout = hasActiveEmergencies ? ReceiptPollingInterval : Timeout.InfiniteTimeSpan;
        if (nextRemovalAt is { } removalAt)
        {
            var untilRemoval = removalAt - DateTimeOffset.UtcNow;
            if (untilRemoval < TimeSpan.Zero)
            {
                untilRemoval = TimeSpan.Zero;
            }

            if (timeout == Timeout.InfiniteTimeSpan || untilRemoval < timeout)
            {
                timeout = untilRemoval;
            }
        }

        return timeout;
    }

    private async Task WaitForSignalAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            await _signal.WaitAsync(timeout, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The loop condition ends the loop.
        }
    }
}
