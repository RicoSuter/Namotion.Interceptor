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
                await _signal.WaitAsync(Timeout.InfiniteTimeSpan, stoppingToken);
                continue;
            }

            try
            {
                // Polling runs also when validation failed, so a transient validation failure cannot leave
                // acknowledgements unseen.
                var isValid = await ValidateTargetsIfRequestedAsync(token, stoppingToken);
                var isPolled = await PollReceiptsAsync(token, stoppingToken);
                if (isValid && isPolled)
                {
                    StatusMessage = null;
                    Status = ServiceStatus.Running;
                }

                var timeout = _emergencyTracker.RemoveFinishedEmergencies();
                await _signal.WaitAsync(isValid && isPolled ? timeout : LimitTo(timeout, RetryInterval), stoppingToken);
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
                await _signal.WaitAsync(Timeout.InfiniteTimeSpan, stoppingToken);
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
            await ValidateTargetsAsync(token, cancellationToken);
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

    /// <summary>
    /// Reads the receipts that are due; returns false when polling failed unexpectedly, with the service in
    /// <see cref="ServiceStatus.Error"/>.
    /// </summary>
    private async Task<bool> PollReceiptsAsync(string token, CancellationToken cancellationToken)
    {
        try
        {
            await _emergencyTracker.PollReceiptsAsync(token, cancellationToken);
            return true;
        }
        catch (Exception exception) when (IsRetriedFailure(exception, cancellationToken))
        {
            _logger.LogError(exception, "Polling the Pushover emergency receipts failed");
            StatusMessage = exception.Message;
            Status = ServiceStatus.Error;
            return false;
        }
    }

    // A rejected token and the stop of the service are handled by the loop.
    private static bool IsRetriedFailure(Exception exception, CancellationToken cancellationToken) =>
        exception is not PushoverException { IsApplicationTokenInvalid: true } &&
        !(exception is OperationCanceledException && cancellationToken.IsCancellationRequested);

    private static TimeSpan LimitTo(TimeSpan timeout, TimeSpan limit) =>
        timeout == Timeout.InfiniteTimeSpan || limit < timeout ? limit : timeout;

}
