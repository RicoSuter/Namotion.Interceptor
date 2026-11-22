using HomeBlaze.Abstractions;
using Microsoft.Extensions.Logging;
using Namotion.Devices.Tesla.Client;
using Namotion.Devices.Tesla.Client.Dto;
using Namotion.Devices.Tesla.Polling;

namespace Namotion.Devices.Tesla;

public partial class TeslaAccount
{
    private const string MissingRefreshTokenMessage = "Missing refresh token";

    private static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(10);

    // Task.Delay throws for delays above about 49.7 days, which would end the loop.
    private static readonly TimeSpan MaximumDelay = TimeSpan.FromDays(1);
    private static readonly TimeSpan WakeUpTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan WakeUpCheckInterval = TimeSpan.FromSeconds(5);

    // Keyed by VIN; only touched while _pollLock is held.
    private readonly Dictionary<string, TeslaVehiclePollingPolicy> _pollingPolicies = new(StringComparer.Ordinal);

    // Replaced by the loop once completed; read with Volatile because ApplyConfigurationAsync runs on another thread.
    private TaskCompletionSource _configurationChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Written by a poll and read by the loop after it.
    private DateTimeOffset _pausedUntil = DateTimeOffset.MinValue;

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
                _logger.LogError(exception, "The polling loop of the Tesla account failed.");
                Status = ServiceStatus.Error;
                StatusMessage = exception.Message;
                delay = Limit(RetryInterval);
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
        if (string.IsNullOrWhiteSpace(RefreshToken))
        {
            Status = ServiceStatus.Stopped;
            StatusMessage = MissingRefreshTokenMessage;
            return Timeout.InfiniteTimeSpan;
        }

        // A configuration change wakes the loop early; only a replaced refresh token ends the pause Tesla asked for.
        var pause = _pausedUntil - _timeProvider.GetUtcNow();
        if (pause > TimeSpan.Zero)
        {
            if (!IsRefreshTokenReplaced())
            {
                return AtMostMaximum(pause);
            }

            _pausedUntil = DateTimeOffset.MinValue;
        }

        await PollOnceAsync(stoppingToken);
        return GetNextPollDelay();
    }

    private void SignalConfigurationChanged() => Volatile.Read(ref _configurationChanged).TrySetResult();

    /// <summary>Waits for the delay, a configuration change or the stop, whichever comes first. Never throws.</summary>
    private async Task WaitAsync(TimeSpan delay, Task configurationChanged, CancellationToken stoppingToken)
    {
        using var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        await Task.WhenAny(Task.Delay(delay, _timeProvider, delayCancellation.Token), configurationChanged);
        await delayCancellation.CancelAsync();
    }

    /// <summary>
    /// The delay until the next poll: none until the configuration changes when re-authentication is required,
    /// else the retry or state polling interval, extended to the pause that Tesla asked for.
    /// </summary>
    internal TimeSpan GetNextPollDelay()
    {
        if (IsReauthenticationRequired)
        {
            return Timeout.InfiniteTimeSpan;
        }

        var delay = Limit(Status == ServiceStatus.Error ? RetryInterval : StatePollingInterval);
        var pause = AtMostMaximum(_pausedUntil - _timeProvider.GetUtcNow());
        return pause > delay ? pause : delay;
    }

    /// <summary>Limits a configured interval to between <see cref="MinimumInterval"/> and <see cref="MaximumDelay"/>.</summary>
    private static TimeSpan Limit(TimeSpan interval) => interval < MinimumInterval ? MinimumInterval : AtMostMaximum(interval);

    private static TimeSpan AtMostMaximum(TimeSpan delay) => delay > MaximumDelay ? MaximumDelay : delay;

    /// <summary>
    /// One polling cycle: reads the vehicle list with the cached snapshots, then the data of each awake vehicle that is due.
    /// Handles every failure except cancellation and reports it in <see cref="Status"/> and <see cref="StatusMessage"/>.
    /// </summary>
    internal async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        await _pollLock.WaitAsync(cancellationToken);
        try
        {
            if (Status == ServiceStatus.Stopped)
            {
                Status = ServiceStatus.Starting;
            }

            await SaveUnsavedRefreshTokenAsync(cancellationToken);
            var tokenManager = GetTokenManager();
            var products = await tokenManager.CallAsync(
                accessToken => _apiClient.GetVehicleProductsAsync(accessToken, cancellationToken), cancellationToken);

            UpdateAuthenticationState(tokenManager);
            var failure = UpdateVehicles(products);
            foreach (var vehicle in Vehicles.Values)
            {
                failure = await ReadVehicleIfDueAsync(tokenManager, vehicle, cancellationToken) ?? failure;
            }

            Status = ServiceStatus.Running;
            StatusMessage = failure;
        }
        catch (TeslaReauthenticationRequiredException exception)
        {
            SetReauthenticationRequired(exception);
        }
        catch (TeslaApiException exception) when (exception.RetryAfter is { } retryAfter)
        {
            _logger.LogWarning(exception, "Tesla limits the requests of the account; pausing for {RetryAfter}.", retryAfter);
            _pausedUntil = _timeProvider.GetUtcNow() + AtMostMaximum(retryAfter);
            Status = ServiceStatus.Error;
            StatusMessage = exception.Message;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(exception, "Polling the Tesla account failed.");
            Status = ServiceStatus.Error;
            StatusMessage = exception.Message;
        }
        finally
        {
            _pollLock.Release();
        }
    }

    /// <summary>
    /// Updates existing vehicles in place and replaces the dictionary only when a vehicle was added or removed.
    /// </summary>
    /// <returns>The message of the last vehicle that failed, or null.</returns>
    private string? UpdateVehicles(TeslaProductDto[] products)
    {
        var vehicles = Vehicles;
        Dictionary<string, TeslaVehicle>? updatedVehicles = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var seenExistingCount = 0;
        string? failure = null;

        foreach (var product in products)
        {
            if (product.Vin is not { Length: > 0 } vin || !seen.Add(vin))
            {
                continue;
            }

            if (vehicles.TryGetValue(vin, out var vehicle))
            {
                seenExistingCount++;
            }
            else
            {
                // Set up before it is attached with the dictionary below.
                vehicle = new TeslaVehicle(product.Id, vin, _timeProvider, _loggerFactory.CreateLogger<TeslaVehicle>());
                vehicle.SetWakeUpHandler(WakeUpAsync);
                updatedVehicles ??= new Dictionary<string, TeslaVehicle>(vehicles, StringComparer.Ordinal);
                updatedVehicles[vin] = vehicle;
                _logger.LogInformation("Discovered Tesla vehicle {Vin}.", vin);
            }

            failure = ApplyProduct(vehicle, product) ?? failure;
        }

        if (seenExistingCount < vehicles.Count)
        {
            updatedVehicles = RemoveVehicles(updatedVehicles ?? vehicles, seen);
        }

        if (updatedVehicles is not null)
        {
            Vehicles = updatedVehicles;
        }

        return failure;
    }

    /// <returns>A copy of the vehicles with only those whose VIN is kept.</returns>
    private Dictionary<string, TeslaVehicle> RemoveVehicles(Dictionary<string, TeslaVehicle> vehicles, HashSet<string> keptVins)
    {
        foreach (var vin in vehicles.Keys.Where(vin => !keptVins.Contains(vin)))
        {
            _pollingPolicies.Remove(vin);
            _logger.LogInformation("Tesla vehicle {Vin} was removed from the account.", vin);
        }

        return vehicles
            .Where(pair => keptVins.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    }

    /// <returns>The failure message, or null.</returns>
    private string? ApplyProduct(TeslaVehicle vehicle, TeslaProductDto product)
    {
        try
        {
            var wasActive = vehicle.IsActive;
            vehicle.ApplyProduct(product);
            if (!wasActive && vehicle.IsActive)
            {
                GetPollingPolicy(vehicle).OnActivityObserved();
            }

            return null;
        }
        catch (Exception exception)
        {
            // One vehicle failing does not stop the others.
            _logger.LogWarning(exception, "Applying the state of Tesla vehicle {Vin} failed.", vehicle.Vin);
            return $"Applying the state of {vehicle.Title} failed: {exception.Message}";
        }
    }

    /// <returns>The failure message, or null.</returns>
    private async Task<string?> ReadVehicleIfDueAsync(TeslaTokenManager tokenManager, TeslaVehicle vehicle, CancellationToken cancellationToken)
    {
        var policy = GetPollingPolicy(vehicle);
        var now = _timeProvider.GetUtcNow();
        if (!policy.ShouldRead(vehicle.VehicleState, now, PollingSettings))
        {
            return null;
        }

        try
        {
            _logger.LogDebug("Reading Tesla vehicle {Vin} (active: {IsActive}).", vehicle.Vin, vehicle.IsActive);
            // The vehicle list accepted the access token moments ago, so a 401 concerns this vehicle only. Refreshing for it
            // would rotate and save the refresh token on every read while the vehicle keeps rejecting it.
            if (!await ReadVehicleAsync(tokenManager, vehicle, refreshOnUnauthorized: false, cancellationToken))
            {
                _logger.LogDebug("Tesla vehicle {Vin} fell asleep before it was read.", vehicle.Vin);
                vehicle.VehicleState = TeslaVehicleState.Asleep;
            }

            return null;
        }
        catch (Exception exception) when (IsVehicleFailure(exception, cancellationToken))
        {
            // One vehicle failing does not stop the others.
            _logger.LogWarning(exception, "Reading Tesla vehicle {Vin} failed.", vehicle.Vin);
            return $"Reading {vehicle.Title} failed: {exception.Message}";
        }
        finally
        {
            // A failed read counts as a read, so that a failing vehicle is not read more often and can still fall asleep.
            policy.OnRead(now, vehicle.IsActive, vehicle.CanSleep, PollingSettings);
        }
    }

    /// <summary>Reads the data of a vehicle and applies it.</summary>
    /// <returns>False when the vehicle is asleep or offline.</returns>
    private async Task<bool> ReadVehicleAsync(TeslaTokenManager tokenManager, TeslaVehicle vehicle, bool refreshOnUnauthorized, CancellationToken cancellationToken)
    {
        var data = await tokenManager.CallAsync(
            accessToken => _apiClient.GetVehicleDataAsync(accessToken, vehicle.Id, cancellationToken), refreshOnUnauthorized, cancellationToken);
        if (data is null)
        {
            return false;
        }

        vehicle.ApplyVehicleData(data);
        return true;
    }

    /// <summary>
    /// Whether a failure concerns only one vehicle. Re-authentication and request limits apply to the whole account and
    /// end the poll; cancellation stops it.
    /// </summary>
    private static bool IsVehicleFailure(Exception exception, CancellationToken cancellationToken) => exception switch
    {
        TeslaReauthenticationRequiredException => false,
        TeslaApiException { RetryAfter: not null } => false,
        OperationCanceledException => !cancellationToken.IsCancellationRequested,
        _ => true
    };

    private TeslaVehiclePollingPolicy GetPollingPolicy(TeslaVehicle vehicle)
    {
        if (!_pollingPolicies.TryGetValue(vehicle.Vin, out var policy))
        {
            policy = new TeslaVehiclePollingPolicy();
            _pollingPolicies[vehicle.Vin] = policy;
        }

        return policy;
    }

    /// <summary>
    /// Wakes a vehicle on request of the user, waits until it is online and reads it. The only caller of wake_up.
    /// Holds the poll lock while it waits, so that polling pauses until the vehicle is awake or the wait times out.
    /// </summary>
    private async Task WakeUpAsync(TeslaVehicle vehicle, CancellationToken cancellationToken)
    {
        if (_isDisposed)
        {
            throw new InvalidOperationException("The Tesla account was disposed; it cannot wake a vehicle anymore.");
        }

        await _pollLock.WaitAsync(cancellationToken);
        try
        {
            var tokenManager = GetTokenManager();
            _logger.LogInformation("Waking Tesla vehicle {Vin} on request of the user.", vehicle.Vin);
            await tokenManager.CallAsync(async accessToken =>
            {
                await _apiClient.WakeUpAsync(accessToken, vehicle.Id, cancellationToken);
                return true;
            }, cancellationToken);

            var deadline = _timeProvider.GetUtcNow() + WakeUpTimeout;
            while (true)
            {
                if (await ReadVehicleAsync(tokenManager, vehicle, refreshOnUnauthorized: true, cancellationToken))
                {
                    var policy = GetPollingPolicy(vehicle);
                    policy.OnActivityObserved();
                    policy.OnRead(_timeProvider.GetUtcNow(), vehicle.IsActive, vehicle.CanSleep, PollingSettings);
                    return;
                }

                if (_timeProvider.GetUtcNow() >= deadline)
                {
                    throw new TimeoutException($"Tesla vehicle {vehicle.Title} did not wake up within {WakeUpTimeout.TotalMinutes} minutes.");
                }

                await Task.Delay(WakeUpCheckInterval, _timeProvider, cancellationToken);
            }
        }
        catch (TeslaReauthenticationRequiredException exception)
        {
            SetReauthenticationRequired(exception);
            throw;
        }
        catch (Exception exception) when (LogWakeUpFailure(exception, vehicle, cancellationToken))
        {
            // Not reached: the filter only logs and lets the exception reach the caller.
            throw;
        }
        finally
        {
            _pollLock.Release();
        }
    }

    private bool LogWakeUpFailure(Exception exception, TeslaVehicle vehicle, CancellationToken cancellationToken)
    {
        // HttpClient reports its own timeout as a canceled task, so only a cancellation by the caller is not a failure.
        if (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "Waking Tesla vehicle {Vin} failed.", vehicle.Vin);
        }

        return false;
    }
}
