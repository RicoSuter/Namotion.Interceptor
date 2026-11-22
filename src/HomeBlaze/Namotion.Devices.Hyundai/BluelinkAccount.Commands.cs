using Microsoft.Extensions.Logging;
using Namotion.Devices.Hyundai.Client;
using Namotion.Devices.Hyundai.Mapping;

namespace Namotion.Devices.Hyundai;

public partial class BluelinkAccount
{
    private const int CommandResultAttempts = 9;

    private static readonly TimeSpan CommandResultInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Ccs2RefreshWait = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan ControlTokenAhead = TimeSpan.FromSeconds(30);

    // Only touched while _lock is held.
    private BluelinkCcsToken? _controlToken;

    bool IBluelinkVehicleCommands.CanSendCommands(BluelinkProtocol protocol) =>
        IsAuthenticated && RequestsToday < DailyRequestLimit && (protocol == BluelinkProtocol.Legacy || !string.IsNullOrEmpty(Pin));

    bool IBluelinkVehicleCommands.CanRefresh(decimal? auxiliaryBatteryLevel) =>
        IsAuthenticated && RequestsToday < DailyRequestLimit && auxiliaryBatteryLevel is { } level && level >= MinimumAuxiliaryBatteryLevel;

    async Task IBluelinkVehicleCommands.RefreshAsync(BluelinkVehicle vehicle, CancellationToken cancellationToken)
    {
        if (vehicle.AuxiliaryBatteryLevel is not { } level || level < MinimumAuxiliaryBatteryLevel)
        {
            throw new InvalidOperationException("Refresh is refused while the 12 V battery is below the minimum level or unknown.");
        }

        await RunAsync(async tokenManager =>
        {
            _logger.LogInformation("Asking the Bluelink vehicle {Vin} to upload its status.", vehicle.Vin);
            await tokenManager.CallAsync(accessToken => _apiClient.RequestStatusAsync(CreateRequest(tokenManager, accessToken, vehicle), cancellationToken), cancellationToken);
            if (vehicle.Protocol == BluelinkProtocol.Ccs2)
            {
                await Task.Delay(Ccs2RefreshWait, _timeProvider, cancellationToken);
            }

            await ReadVehicleAsync(tokenManager, vehicle, cancellationToken);
        }, cancellationToken);
    }

    Task IBluelinkVehicleCommands.SetChargingAsync(BluelinkVehicle vehicle, bool start, CancellationToken cancellationToken) =>
        RunCommandAsync(vehicle, needsPin: true,
            (request, controlToken) => _apiClient.SetChargingAsync(request, start, controlToken, cancellationToken), cancellationToken);

    Task IBluelinkVehicleCommands.SetChargeLimitsAsync(BluelinkVehicle vehicle, int acPercent, int dcPercent, CancellationToken cancellationToken) =>
        RunCommandAsync(vehicle, needsPin: false,
            (request, _) => _apiClient.SetChargeTargetsAsync(request, acPercent, dcPercent, cancellationToken), cancellationToken);

    Task IBluelinkVehicleCommands.StartClimateAsync(BluelinkVehicle vehicle, decimal temperature, bool defrost, CancellationToken cancellationToken)
    {
        // Validated before any call, so that a wrong configuration never costs a PIN exchange.
        int minutes;
        try
        {
            minutes = BluelinkValues.ToClimateMinutes(ClimateDuration);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new InvalidOperationException("The climate duration of the account must be whole minutes from 1 to 30.", exception);
        }

        return RunCommandAsync(vehicle, needsPin: true,
            (request, controlToken) => _apiClient.StartClimateAsync(request, temperature, defrost, minutes, controlToken, cancellationToken), cancellationToken);
    }

    Task IBluelinkVehicleCommands.StopClimateAsync(BluelinkVehicle vehicle, CancellationToken cancellationToken) =>
        RunCommandAsync(vehicle, needsPin: true,
            (request, controlToken) => _apiClient.StopClimateAsync(request, controlToken, cancellationToken), cancellationToken);

    /// <summary>
    /// Sends a command, waits for its result in the notification records and reads the stored status once. A CCS2
    /// command that needs the PIN fails before any call when no PIN is stored.
    /// </summary>
    private async Task RunCommandAsync(BluelinkVehicle vehicle, bool needsPin, Func<BluelinkVehicleRequest, string?, Task<string>> send, CancellationToken cancellationToken)
    {
        var needsControlToken = needsPin && vehicle.Protocol == BluelinkProtocol.Ccs2;
        if (needsControlToken && string.IsNullOrEmpty(Pin))
        {
            throw new InvalidOperationException("A PIN is required for commands of this vehicle; enter it in the account settings.");
        }

        await RunAsync(async tokenManager =>
        {
            _logger.LogInformation("Sending a command to the Bluelink vehicle {Vin}.", vehicle.Vin);
            var messageId = await tokenManager.CallAsync(async accessToken =>
            {
                var controlToken = needsControlToken ? await GetControlTokenAsync(accessToken, tokenManager.DeviceId, cancellationToken) : null;
                return await send(CreateRequest(tokenManager, accessToken, vehicle), controlToken);
            }, cancellationToken);

            await WaitForCommandResultAsync(tokenManager, vehicle, messageId, cancellationToken);
            await ReadVehicleAsync(tokenManager, vehicle, cancellationToken);
        }, cancellationToken);
    }

    /// <summary>Runs a refresh or command under the account lock, after checks that fail before any call.</summary>
    /// <exception cref="BluelinkRequestLimitException">The daily request limit is reached; nothing was sent.</exception>
    private async Task RunAsync(Func<BluelinkTokenManager, Task> action, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        BluelinkTokenManager? tokenManager = null;
        try
        {
            if (string.IsNullOrWhiteSpace(Email) || string.IsNullOrWhiteSpace(Tokens))
            {
                throw new InvalidOperationException("The Bluelink account has no credentials.");
            }

            if (!IsAuthenticated)
            {
                throw new InvalidOperationException("The Bluelink account is not connected.");
            }

            if (_budget.Remaining == 0)
            {
                throw new BluelinkRequestLimitException();
            }

            if (PausedUntil > _timeProvider.GetUtcNow())
            {
                throw new InvalidOperationException("Requests of the Bluelink account are paused after Hyundai's request limit was reached.");
            }

            await EnsureDeviceIdAsync(cancellationToken);
            tokenManager = GetTokenManager();
            await tokenManager.EnsureAccessTokenAsync(cancellationToken);
            await action(tokenManager);
            UpdateAuthenticationState(tokenManager);
        }
        catch (Exception exception) when (exception is not OperationCanceledException && IsAccountFailure(exception, cancellationToken))
        {
            HandleAccountFailure(exception, tokenManager);
            throw;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task WaitForCommandResultAsync(BluelinkTokenManager tokenManager, BluelinkVehicle vehicle, string messageId, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < CommandResultAttempts; attempt++)
        {
            await Task.Delay(CommandResultInterval, _timeProvider, cancellationToken);
            var result = await tokenManager.CallAsync(
                accessToken => _apiClient.GetCommandResultAsync(CreateRequest(tokenManager, accessToken, vehicle), messageId, cancellationToken), cancellationToken);

            if (result == BluelinkCommandResult.Success)
            {
                return;
            }

            if (result != BluelinkCommandResult.Pending)
            {
                throw new BluelinkCommandException(result);
            }
        }

        throw new BluelinkCommandException(BluelinkCommandResult.TimedOut);
    }

    /// <summary>Returns the cached control token, or exchanges the PIN for a new one.</summary>
    private async Task<string> GetControlTokenAsync(string accessToken, string deviceId, CancellationToken cancellationToken)
    {
        if (_controlToken is { } cached && _timeProvider.GetUtcNow() < cached.ExpiresAt - ControlTokenAhead)
        {
            return cached.AccessToken;
        }

        try
        {
            _controlToken = await _apiClient.GetControlTokenAsync(accessToken, deviceId, Pin, cancellationToken);
            return _controlToken.AccessToken;
        }
        catch (BluelinkPinRejectedException exception)
        {
            // No retry: five wrong PINs lock PIN entry for a while.
            _controlToken = null;
            throw new InvalidOperationException("Hyundai rejected the PIN. Check the PIN in the account settings; five wrong PINs lock PIN entry for a while.", exception);
        }
    }
}
