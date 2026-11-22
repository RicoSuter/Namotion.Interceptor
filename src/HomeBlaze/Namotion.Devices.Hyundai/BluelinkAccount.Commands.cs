using HomeBlaze.Abstractions;
using Microsoft.Extensions.Logging;
using Namotion.Devices.Hyundai.Client;
using Namotion.Devices.Hyundai.Mapping;

namespace Namotion.Devices.Hyundai;

public partial class BluelinkAccount
{
    private const int CommandResultAttempts = 9;

    // The most vehicle API calls of each operation. An operation fails before any call when fewer are left, so that a
    // started one never runs out of requests mid-way. Not covered: a retry after a token expired answer costs one more
    // call, and a DailyRequestLimit lowered while an operation runs.
    private const int ReadVehicleCost = 2; // stored status and parking location
    private const int RefreshCost = 1 + ReadVehicleCost;
    private const int CommandCost = 1 + CommandResultAttempts + ReadVehicleCost;
    private const int PinExchangeCost = 1;
    private const int VehicleListCost = 1;
    private const int DeviceRegistrationCost = 1;

    // Polling keeps the calls of the most expensive command.
    private const int CommandReserve = CommandCost + PinExchangeCost;

    private static readonly TimeSpan CommandResultInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Ccs2RefreshWait = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan ControlTokenAhead = TimeSpan.FromSeconds(30);

    // Only touched while _lock is held.
    private BluelinkCcsToken? _controlToken;

    // Reads RequestsToday rather than the budget, so that the derived IsEnabled attributes update with it.
    private bool CanSendRequests(int cost) => IsAuthenticated && DailyRequestLimit - RequestsToday >= WithDeviceRegistration(cost);

    bool IBluelinkVehicleCommands.CanSendCommands(BluelinkProtocol protocol) =>
        CanSendRequests(GetCommandCost(needsControlToken: protocol == BluelinkProtocol.Ccs2)) &&
        (protocol == BluelinkProtocol.Legacy || !string.IsNullOrEmpty(Pin));

    bool IBluelinkVehicleCommands.CanRefresh(decimal? auxiliaryBatteryLevel) =>
        CanSendRequests(RefreshCost) && IsAuxiliaryBatteryAtMinimum(auxiliaryBatteryLevel);

    private static int GetCommandCost(bool needsControlToken) => needsControlToken ? CommandCost + PinExchangeCost : CommandCost;

    /// <summary>Adds the device registration that a poll or operation makes first when no device ID is stored.</summary>
    private int WithDeviceRegistration(int cost) => string.IsNullOrEmpty(DeviceId) ? cost + DeviceRegistrationCost : cost;

    async Task IBluelinkVehicleCommands.RefreshAsync(BluelinkVehicle vehicle, CancellationToken cancellationToken)
    {
        if (!IsAuxiliaryBatteryAtMinimum(vehicle.AuxiliaryBatteryLevel))
        {
            throw new InvalidOperationException("Refresh is refused while the 12 V battery is below the minimum level or unknown.");
        }

        await RunAsync(vehicle, "Refresh", RefreshCost, async tokenManager =>
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

    private bool IsAuxiliaryBatteryAtMinimum(decimal? level) => level >= MinimumAuxiliaryBatteryLevel;

    Task IBluelinkVehicleCommands.SetChargingAsync(BluelinkVehicle vehicle, bool start, CancellationToken cancellationToken) =>
        RunCommandAsync(vehicle, start ? "StartCharging" : "StopCharging", needsPin: true,
            (request, controlToken) => _apiClient.SetChargingAsync(request, start, controlToken, cancellationToken), cancellationToken);

    Task IBluelinkVehicleCommands.SetChargeLimitsAsync(BluelinkVehicle vehicle, int acPercent, int dcPercent, CancellationToken cancellationToken) =>
        RunCommandAsync(vehicle, "SetChargeLimits", needsPin: false,
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

        return RunCommandAsync(vehicle, "StartClimate", needsPin: true,
            (request, controlToken) => _apiClient.StartClimateAsync(request, temperature, defrost, minutes, controlToken, cancellationToken), cancellationToken);
    }

    Task IBluelinkVehicleCommands.StopClimateAsync(BluelinkVehicle vehicle, CancellationToken cancellationToken) =>
        RunCommandAsync(vehicle, "StopClimate", needsPin: true,
            (request, controlToken) => _apiClient.StopClimateAsync(request, controlToken, cancellationToken), cancellationToken);

    /// <summary>
    /// Sends a command, waits for its result in the notification records and reads the stored status once. A CCS2
    /// command that needs the PIN fails before any call when no PIN is stored. Once the vehicle confirmed the command,
    /// a failing status read is logged and does not fail the command, unless it concerns the whole account.
    /// </summary>
    private async Task RunCommandAsync(BluelinkVehicle vehicle, string operation, bool needsPin, Func<BluelinkVehicleRequest, string?, Task<string>> send, CancellationToken cancellationToken)
    {
        var needsControlToken = needsPin && vehicle.Protocol == BluelinkProtocol.Ccs2;
        if (needsControlToken && string.IsNullOrEmpty(Pin))
        {
            throw new InvalidOperationException("A PIN is required for commands of this vehicle; enter it in the account settings.");
        }

        await RunAsync(vehicle, operation, GetCommandCost(needsControlToken), async tokenManager =>
        {
            _logger.LogInformation("Sending a command to the Bluelink vehicle {Vin}.", vehicle.Vin);
            var messageId = await tokenManager.CallAsync(async accessToken =>
            {
                var controlToken = needsControlToken ? await GetControlTokenAsync(accessToken, tokenManager.DeviceId, cancellationToken) : null;
                return await send(CreateRequest(tokenManager, accessToken, vehicle), controlToken);
            }, cancellationToken);

            await WaitForCommandResultAsync(tokenManager, vehicle, messageId, cancellationToken);
            try
            {
                await ReadVehicleAsync(tokenManager, vehicle, cancellationToken);
            }
            catch (Exception exception) when (exception is BluelinkRequestLimitException || !IsAccountFailure(exception, cancellationToken))
            {
                // A reached limit is reported by the next poll.
                _logger.LogWarning(exception, "The Bluelink vehicle {Vin} executed the command {Operation}, but reading its status afterwards failed.", vehicle.Vin, operation);
            }
        }, cancellationToken);
    }

    /// <summary>Runs a refresh or command of the vehicle; the vehicle is busy from before the account lock is awaited until it ends.</summary>
    /// <param name="vehicle">The vehicle.</param>
    /// <param name="operation">The name of the operation, for the log.</param>
    /// <param name="cost">The most vehicle API calls of the operation, without a device registration.</param>
    /// <param name="action">The calls of the operation.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <exception cref="InvalidOperationException">Another operation of the vehicle is running; nothing was sent.</exception>
    private async Task RunAsync(BluelinkVehicle vehicle, string operation, int cost, Func<BluelinkTokenManager, Task> action, CancellationToken cancellationToken)
    {
        if (!vehicle.TryStartOperation())
        {
            throw new InvalidOperationException("Another operation of this vehicle is running.");
        }

        try
        {
            await RunLockedAsync(vehicle, operation, cost, action, cancellationToken);
        }
        finally
        {
            vehicle.EndOperation();
        }
    }

    /// <summary>
    /// Runs a refresh or command under the account lock, after checks that fail before any call. A failure after the
    /// checks is logged, unless it is the caller's cancellation or a failure of the whole account.
    /// </summary>
    /// <exception cref="BluelinkRequestLimitException">
    /// The daily request limit is reached, which sets the account status to <see cref="ServiceStatus.Error"/>; or fewer
    /// requests are left than <paramref name="cost"/> plus a device registration when no device ID is stored, which
    /// leaves the account status unchanged. Nothing was sent.
    /// </exception>
    private async Task RunLockedAsync(BluelinkVehicle vehicle, string operation, int cost, Func<BluelinkTokenManager, Task> action, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        BluelinkTokenManager? tokenManager = null;
        var checksPassed = false;
        try
        {
            if (!HasCredentials)
            {
                throw new InvalidOperationException("The Bluelink account has no credentials.");
            }

            if (!IsAuthenticated)
            {
                throw new InvalidOperationException("The Bluelink account is not connected.");
            }

            var remaining = _budget.Remaining;
            if (remaining == 0)
            {
                throw new BluelinkRequestLimitException();
            }

            if (PausedUntil > _timeProvider.GetUtcNow())
            {
                throw new InvalidOperationException("Requests of the Bluelink account are paused after Hyundai's request limit was reached.");
            }

            if (remaining < WithDeviceRegistration(cost))
            {
                throw BluelinkRequestLimitException.CreateTooFewRequests();
            }

            checksPassed = true;
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
        catch (Exception exception) when (checksPassed && (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested))
        {
            LogOperationFailure(vehicle, operation, exception);
            throw;
        }
        finally
        {
            _lock.Release();
        }
    }

    private void LogOperationFailure(BluelinkVehicle vehicle, string operation, Exception exception) =>
        _logger.LogWarning(exception, "The Bluelink operation {Operation} of the vehicle {Vin} failed.", operation, vehicle.Vin);

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
