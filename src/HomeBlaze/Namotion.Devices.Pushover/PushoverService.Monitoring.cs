using HomeBlaze.Abstractions;
using Microsoft.Extensions.Logging;

namespace Namotion.Devices.Pushover;

public partial class PushoverService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // Consumed before validating, so only a change during the validation requests another one.
            await _validationSignal.WaitAsync(TimeSpan.Zero, CancellationToken.None);

            var token = ApplicationToken;
            if (string.IsNullOrEmpty(token))
            {
                StatusMessage = "No application token configured";
                Status = ServiceStatus.Stopped;
                await WaitForValidationRequestAsync(Timeout.InfiniteTimeSpan, stoppingToken);
                continue;
            }

            try
            {
                Status = ServiceStatus.Starting;
                StatusMessage = "Validating targets...";
                await ValidateTargetsAsync(token, stoppingToken);
                StatusMessage = null;
                Status = ServiceStatus.Running;

                await WaitForValidationRequestAsync(Timeout.InfiniteTimeSpan, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (PushoverException exception) when (exception.IsApplicationTokenInvalid)
            {
                _logger.LogError(exception, "Pushover rejected the application token");
                StatusMessage = "The application token is invalid";
                Status = ServiceStatus.Error;

                // Retrying cannot help until the token changes, which requests a validation.
                await WaitForValidationRequestAsync(Timeout.InfiniteTimeSpan, stoppingToken);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Validating the Pushover targets failed");
                StatusMessage = exception.Message;
                Status = ServiceStatus.Error;
                await WaitForValidationRequestAsync(RetryInterval, stoppingToken);
            }
        }

        StatusMessage = null;
        Status = ServiceStatus.Stopped;
    }

    private async Task ValidateTargetsAsync(string token, CancellationToken cancellationToken)
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
    }

    // A rejection with 4xx except 429 cannot succeed on retry.
    private static bool IsPermanentValidationFailure(PushoverException exception) =>
        exception is { IsApplicationTokenInvalid: false, StatusCode: { } statusCode } &&
        (int)statusCode is >= 400 and < 500 and not 429;

    private async Task WaitForValidationRequestAsync(TimeSpan timeout, CancellationToken stoppingToken)
    {
        try
        {
            await _validationSignal.WaitAsync(timeout, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The loop condition ends the loop.
        }
    }
}
