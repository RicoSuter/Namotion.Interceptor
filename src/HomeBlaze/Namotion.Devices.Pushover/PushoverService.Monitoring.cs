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
                Status = ServiceStatus.Stopped;
                StatusMessage = "No application token configured";
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

                Status = ServiceStatus.Running;
                StatusMessage = null;

                var hasOpenEmergencies = await PollReceiptsAsync(token, stoppingToken);
                await WaitForSignalAsync(hasOpenEmergencies ? ReceiptPollingInterval : Timeout.InfiniteTimeSpan, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (PushoverException exception) when (exception.IsApplicationTokenInvalid)
            {
                _logger.LogError(exception, "Pushover rejected the application token");
                _isValidationRequested = true;
                Status = ServiceStatus.Error;
                StatusMessage = "The application token is invalid";

                // Retrying cannot help until the token changes, which signals the loop.
                await WaitForSignalAsync(Timeout.InfiniteTimeSpan, stoppingToken);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Pushover service failed");
                _isValidationRequested = true;
                Status = ServiceStatus.Error;
                StatusMessage = exception.Message;
                await WaitForSignalAsync(RetryInterval, stoppingToken);
            }
        }

        Status = ServiceStatus.Stopped;
        StatusMessage = null;
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
                _logger.LogWarning(exception, "Pushover target {Target} failed validation", target.Title);
                target.SetValidationFailed(exception.Message);
            }
        }
    }

    private async Task<bool> PollReceiptsAsync(string token, CancellationToken cancellationToken)
    {
        var hasOpenEmergencies = false;
        foreach (var target in Targets.Values)
        {
            if (target.EmergencyReceipt is not { } receipt)
            {
                continue;
            }

            try
            {
                var response = await _client.GetReceiptAsync(token, receipt, cancellationToken);
                target.ApplyReceipt(receipt, response);
            }
            catch (PushoverException exception)
            {
                _logger.LogWarning(exception, "Reading the emergency receipt of Pushover target {Target} failed", target.Title);
                target.ApplyReceiptError(receipt, DateTimeOffset.UtcNow);
            }

            hasOpenEmergencies |= target.IsEmergencyActive;
        }

        return hasOpenEmergencies;
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
