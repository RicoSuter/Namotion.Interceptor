using System.Globalization;
using HomeBlaze.Abstractions;
using Microsoft.Extensions.Logging;
using Namotion.Devices.Hyundai.Client;
using Namotion.Devices.Hyundai.Mapping;

namespace Namotion.Devices.Hyundai;

public partial class BluelinkAccount
{
    private const string MissingCredentialsMessage = "Missing credentials";
    private const int CommandReserve = 10;

    private static readonly TimeSpan MinimumPollingInterval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan MinimumRetryInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan VehicleListInterval = TimeSpan.FromHours(24);
    private static readonly TimeSpan RateLimitPause = TimeSpan.FromHours(2);

    // Replaced by the loop once completed; read with Volatile because ApplyConfigurationAsync runs on another thread.
    private TaskCompletionSource _configurationChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // The end of the pause after Hyundai's request limit (5091) in UTC ticks. Written while _lock is held and read by
    // the loop without it, hence Volatile.
    private long _pausedUntilTicks;

    // Only touched while _lock is held.
    private DateTimeOffset? _vehicleListReadAt;

    private DateTimeOffset PausedUntil
    {
        get => new(Volatile.Read(ref _pausedUntilTicks), TimeSpan.Zero);
        set => Volatile.Write(ref _pausedUntilTicks, value.UtcTicks);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var configurationChanged = Volatile.Read(ref _configurationChanged);
            TimeSpan delay;
            try
            {
                delay = await RunIterationAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // PollOnceAsync handles its own failures; this keeps the loop alive on anything else.
                _logger.LogError(exception, "The polling loop of the Bluelink account failed.");
                Status = ServiceStatus.Error;
                StatusMessage = exception.Message;
                delay = AtLeast(RetryInterval, MinimumRetryInterval);
            }

            await WaitAsync(delay, configurationChanged.Task, stoppingToken);

            if (configurationChanged.Task.IsCompleted)
            {
                Interlocked.CompareExchange(ref _configurationChanged, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), configurationChanged);
            }
        }

        Status = ServiceStatus.Stopped;
        StatusMessage = null;
    }

    /// <returns>The delay until the next iteration.</returns>
    internal async Task<TimeSpan> RunIterationAsync(CancellationToken stoppingToken)
    {
        if (!HasCredentials)
        {
            Status = ServiceStatus.Stopped;
            StatusMessage = MissingCredentialsMessage;
            ClearAuthenticationState();
            return Timeout.InfiniteTimeSpan;
        }

        if (IsWaitingForNewCredentials())
        {
            return Timeout.InfiniteTimeSpan;
        }

        var pause = PausedUntil - _timeProvider.GetUtcNow();
        if (pause > TimeSpan.Zero)
        {
            return pause;
        }

        var remaining = _budget.Remaining;
        if (remaining == 0)
        {
            Status = ServiceStatus.Error;
            StatusMessage = BluelinkRequestLimitException.LimitMessage;
            return _budget.TimeUntilReset;
        }

        if (remaining < CommandReserve)
        {
            Status = ServiceStatus.Running;
            StatusMessage = "Polling pauses until midnight to keep the remaining requests for commands.";
            return _budget.TimeUntilReset;
        }

        await PollOnceAsync(stoppingToken);
        return GetNextPollDelay();
    }

    private bool HasCredentials => !string.IsNullOrWhiteSpace(Email) && !string.IsNullOrWhiteSpace(Tokens);

    private void SignalConfigurationChanged() => Volatile.Read(ref _configurationChanged).TrySetResult();

    /// <summary>Waits for the delay, a configuration change or the stop, whichever comes first. Never throws.</summary>
    private async Task WaitAsync(TimeSpan delay, Task configurationChanged, CancellationToken stoppingToken)
    {
        using var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        await Task.WhenAny(Task.Delay(delay, _timeProvider, delayCancellation.Token), configurationChanged);
        await delayCancellation.CancelAsync();
    }

    internal TimeSpan GetNextPollDelay()
    {
        if (IsWaitingForNewCredentials())
        {
            return Timeout.InfiniteTimeSpan;
        }

        var delay = Status == ServiceStatus.Error
            ? AtLeast(RetryInterval, MinimumRetryInterval)
            : AtLeast(StatusPollingInterval, MinimumPollingInterval);
        var pause = PausedUntil - _timeProvider.GetUtcNow();
        return pause > delay ? pause : delay;
    }

    private static TimeSpan AtLeast(TimeSpan interval, TimeSpan minimum) => interval < minimum ? minimum : interval;

    /// <summary>
    /// One polling cycle: the vehicle list when it is due, then the stored status and parking location of every vehicle.
    /// Handles every failure except cancellation and reports it in <see cref="Status"/> and <see cref="StatusMessage"/>.
    /// </summary>
    internal async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        BluelinkTokenManager? tokenManager = null;
        try
        {
            if (Status == ServiceStatus.Stopped)
            {
                Status = ServiceStatus.Starting;
            }

            await SaveUnsavedConfigurationAsync(cancellationToken);
            await EnsureDeviceIdAsync(cancellationToken);
            tokenManager = GetTokenManager();
            await tokenManager.EnsureAccessTokenAsync(cancellationToken);
            await ReadVehiclesAsync(tokenManager, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            HandleAccountFailure(exception, tokenManager);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Reads the vehicle list when it is due, then the stored status and parking location of every vehicle.</summary>
    /// <remarks>Call it only while <see cref="_lock"/> is held.</remarks>
    private async Task ReadVehiclesAsync(BluelinkTokenManager tokenManager, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        if (_vehicleListReadAt is not { } readAt || now - readAt >= VehicleListInterval)
        {
            var vehicles = await tokenManager.CallAsync(accessToken => _apiClient.GetVehiclesAsync(accessToken, tokenManager.DeviceId, cancellationToken), cancellationToken);
            UpdateVehicles(vehicles);
            _vehicleListReadAt = now;
        }

        string? failure = null;
        foreach (var vehicle in Vehicles.Values)
        {
            try
            {
                await ReadVehicleAsync(tokenManager, vehicle, cancellationToken);
            }
            catch (Exception exception) when (!IsAccountFailure(exception, cancellationToken))
            {
                _logger.LogWarning(exception, "Reading the Bluelink vehicle {Vin} failed.", vehicle.Vin);
                failure = $"{vehicle.Title}: {exception.Message}";
            }
        }

        UpdateAuthenticationState(tokenManager);
        Status = ServiceStatus.Running;
        StatusMessage = failure;
    }

    /// <summary>Reads the stored status and the stored parking location; a failing location keeps the previous one.</summary>
    /// <remarks>Call it only while <see cref="_lock"/> is held.</remarks>
    private async Task ReadVehicleAsync(BluelinkTokenManager tokenManager, BluelinkVehicle vehicle, CancellationToken cancellationToken)
    {
        var state = await tokenManager.CallAsync(accessToken => _apiClient.GetStoredStatusAsync(CreateRequest(tokenManager, accessToken, vehicle), cancellationToken), cancellationToken);
        vehicle.ApplyStatus(BluelinkStatusParser.Parse(state), _timeProvider.GetUtcNow());

        try
        {
            var location = await tokenManager.CallAsync(accessToken => _apiClient.GetParkLocationAsync(CreateRequest(tokenManager, accessToken, vehicle), cancellationToken), cancellationToken);
            vehicle.ApplyLocation(location);
        }
        catch (Exception exception) when (exception is BluelinkApiException or TimeoutException or HttpRequestException && !IsAccountFailure(exception, cancellationToken))
        {
            // Best effort: some accounts answer the parking location with an error, and the status was already applied.
            _logger.LogDebug(exception, "Reading the parking location of the Bluelink vehicle {Vin} failed.", vehicle.Vin);
        }
    }

    private static BluelinkVehicleRequest CreateRequest(BluelinkTokenManager tokenManager, string accessToken, BluelinkVehicle vehicle) =>
        new(accessToken, tokenManager.DeviceId, vehicle.VehicleId, vehicle.Protocol);

    /// <summary>Whether a failure concerns the whole account, so that it stops the cycle instead of one vehicle.</summary>
    private static bool IsAccountFailure(Exception exception, CancellationToken cancellationToken) => exception switch
    {
        OperationCanceledException => cancellationToken.IsCancellationRequested,
        BluelinkReauthenticationRequiredException or BluelinkRequestLimitException or BluelinkCredentialsReplacedException => true,
        // A second token expired answer, after the refresh, concerns the account and not one vehicle.
        BluelinkApiException apiException => apiException.IsRateLimited || apiException.IsDeviceNotRegistered || apiException.IsFingerprintRejected || apiException.IsTokenExpired,
        _ => false
    };

    /// <summary>Reports a failure of the whole account; a failure of credentials replaced meanwhile is an ordinary failure.</summary>
    /// <param name="exception">The failure.</param>
    /// <param name="tokenManager">The manager in use, or null when none could be created.</param>
    /// <remarks>Call it only while <see cref="_lock"/> is held.</remarks>
    private void HandleAccountFailure(Exception exception, BluelinkTokenManager? tokenManager)
    {
        switch (exception)
        {
            case BluelinkReauthenticationRequiredException reauthentication:
                HandleRejection(reauthentication, tokenManager);
                break;

            case BluelinkCredentialsReplacedException:
                ReportCredentialsReplaced();
                break;

            case BluelinkApiException { IsRateLimited: true }:
                PausedUntil = _timeProvider.GetUtcNow() + RateLimitPause;
                _logger.LogWarning("Hyundai's request limit was reached; pausing the Bluelink account for {Pause}.", RateLimitPause);
                Status = ServiceStatus.Error;
                StatusMessage = $"Hyundai's request limit was reached; polling resumes at {(_timeProvider.GetLocalNow() + RateLimitPause).ToString("HH:mm", CultureInfo.InvariantCulture)}.";
                break;

            case BluelinkRequestLimitException:
                // No pause: the next iteration waits until midnight, unless the limit was raised meanwhile.
                Status = ServiceStatus.Error;
                StatusMessage = BluelinkRequestLimitException.LimitMessage;
                break;

            case BluelinkApiException { IsDeviceNotRegistered: true }:
                ResetDeviceId(tokenManager);
                Status = ServiceStatus.Error;
                StatusMessage = exception.Message;
                break;

            default:
                _logger.Log(exception is BluelinkApiException or HttpRequestException or TimeoutException ? LogLevel.Warning : LogLevel.Error,
                    exception, "The Bluelink account failed.");
                Status = ServiceStatus.Error;
                StatusMessage = exception.Message;
                break;
        }
    }

    /// <summary>
    /// Adds new electric vehicles, removes vanished ones and updates known ones in place; replaces the dictionary only
    /// when a vehicle was added or removed. Other vehicle types are skipped.
    /// </summary>
    /// <remarks>Call it only while <see cref="_lock"/> is held.</remarks>
    private void UpdateVehicles(IReadOnlyList<BluelinkVehicleInfo> infos)
    {
        var vehicles = Vehicles;
        var updatedVehicles = new Dictionary<string, BluelinkVehicle>(StringComparer.Ordinal);
        var isChanged = false;

        foreach (var info in infos)
        {
            if (!info.IsElectric)
            {
                _logger.LogDebug("Skipping the Bluelink vehicle {Vin} of type {Type}; only electric vehicles are supported.", info.Vin, info.Type);
                continue;
            }

            if (updatedVehicles.ContainsKey(info.Vin))
            {
                continue;
            }

            if (vehicles.TryGetValue(info.Vin, out var vehicle))
            {
                vehicle.UpdateInfo(info);
            }
            else
            {
                vehicle = new BluelinkVehicle(this, info);
                isChanged = true;
                _logger.LogInformation("Discovered Bluelink vehicle {Vin}.", info.Vin);
            }

            updatedVehicles[info.Vin] = vehicle;
        }

        if (isChanged || updatedVehicles.Count != vehicles.Count)
        {
            Vehicles = updatedVehicles;
        }
    }
}
