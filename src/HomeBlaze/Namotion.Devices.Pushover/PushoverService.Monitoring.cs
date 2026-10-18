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
                // Polling runs also when validation failed, so a transient validation failure cannot leave
                // acknowledgments unseen.
                var isValid = await ValidateTargetsIfRequestedAsync(token, stoppingToken);
                await _emergencyTracker.PollReceiptsAsync(token, stoppingToken);
                if (isValid)
                {
                    StatusMessage = null;
                    Status = ServiceStatus.Running;
                }

                var timeout = _emergencyTracker.GetTimeUntilNextReceiptPoll();
                await WaitForSignalAsync(isValid ? timeout : LimitTo(timeout, RetryInterval), stoppingToken);
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
        }

        StatusMessage = null;
        Status = ServiceStatus.Stopped;
    }

    /// <summary>
    /// Validates the targets when requested; returns false when that failed and is retried, with the service in
    /// <see cref="ServiceStatus.Error"/>.
    /// </summary>
    private async Task<bool> ValidateTargetsIfRequestedAsync(string token, CancellationToken cancellationToken)
    {
        if (!_isValidationRequested)
        {
            return true;
        }

        // Cleared before validating, so a change during validation requests another one.
        _isValidationRequested = false;
        Status = ServiceStatus.Starting;
        StatusMessage = "Validating targets...";
        try
        {
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
                catch (PushoverException exception) when (IsPermanentValidationFailure(exception))
                {
                    _logger.LogWarning(exception, "Pushover target {Target} failed validation", target.Title);
                    target.SetValidationFailed(exception.Message);
                }
            }

            return true;
        }
        catch (Exception exception) when (IsRetriedFailure(exception, cancellationToken))
        {
            _logger.LogError(exception, "Validating the Pushover targets failed");
            _isValidationRequested = true;
            StatusMessage = exception.Message;
            Status = ServiceStatus.Error;
            return false;
        }
    }

    // A rejection with 4xx except 429 cannot succeed on retry.
    private static bool IsPermanentValidationFailure(PushoverException exception) =>
        exception is { IsApplicationTokenInvalid: false, StatusCode: { } statusCode } &&
        (int)statusCode is >= 400 and < 500 and not 429;

    /// <summary>
    /// Whether a failure is retried later: every failure except a rejected application token and the stop of the
    /// service, which the loop handles.
    /// </summary>
    internal static bool IsRetriedFailure(Exception exception, CancellationToken stoppingToken) =>
        exception is not PushoverException { IsApplicationTokenInvalid: true } &&
        !(exception is OperationCanceledException && stoppingToken.IsCancellationRequested);

    private static TimeSpan LimitTo(TimeSpan timeout, TimeSpan limit) =>
        timeout == Timeout.InfiniteTimeSpan || limit < timeout ? limit : timeout;

    private async Task WaitForSignalAsync(TimeSpan timeout, CancellationToken stoppingToken)
    {
        try
        {
            await _signal.WaitAsync(timeout, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The loop condition ends the loop.
        }
    }
}
