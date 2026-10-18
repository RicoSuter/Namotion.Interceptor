using HomeBlaze.Abstractions;
using Microsoft.Extensions.Logging;
using Namotion.Devices.Nuki.Client;
using Namotion.Devices.Nuki.Model;

namespace Namotion.Devices.Nuki;

public partial class NukiBridge
{
    private readonly SemaphoreSlim _configurationChangedSignal = new(0, 1);

    // The bridge handles one request at a time and answers 503 otherwise. Every request holds this gate, and so does the
    // merge of its result, so that an older device list never overwrites a newer one.
    private readonly SemaphoreSlim _requestGate = new(1, 1);

    private volatile NukiBridgeClient? _client;

    /// <inheritdoc />
    public Task ApplyConfigurationAsync(CancellationToken cancellationToken)
    {
        try
        {
            _configurationChangedSignal.Release();
        }
        catch (SemaphoreFullException)
        {
            // A change is already pending.
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Gets the interval a bridge runs with for a configured interval: the configured interval clamped to
    /// <see cref="MinimumIntervalSeconds"/> through <see cref="MaximumIntervalSeconds"/>, or the fallback when it is
    /// zero or negative.
    /// </summary>
    /// <param name="configured">The configured interval, such as <see cref="PollingInterval"/>.</param>
    /// <param name="fallback">The interval to use when the configured one is zero or negative.</param>
    /// <returns>The effective interval.</returns>
    public static TimeSpan GetEffectiveInterval(TimeSpan configured, TimeSpan fallback) =>
        configured <= TimeSpan.Zero
            ? fallback
            : TimeSpan.FromTicks(Math.Clamp(
                configured.Ticks,
                MinimumIntervalSeconds * TimeSpan.TicksPerSecond,
                MaximumIntervalSeconds * TimeSpan.TicksPerSecond));

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (true)
            {
                stoppingToken.ThrowIfCancellationRequested();
                var hostAddress = HostAddress;
                var token = Token;
                if (string.IsNullOrWhiteSpace(hostAddress) || string.IsNullOrEmpty(token))
                {
                    StatusMessage = "Host address and token are required.";
                    Status = ServiceStatus.Error;
                    await _configurationChangedSignal.WaitAsync(stoppingToken);
                    continue;
                }

                if (!await ConnectAndPollAsync(hostAddress, token, stoppingToken))
                {
                    await _configurationChangedSignal.WaitAsync(GetEffectiveInterval(RetryInterval, DefaultRetryInterval), stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopped.
        }

        StatusMessage = null;
        Status = ServiceStatus.Stopped;
    }

    /// <summary>
    /// Connects and polls until the configuration changes (true) or the connection fails (false).
    /// </summary>
    private async Task<bool> ConnectAndPollAsync(string hostAddress, string token, CancellationToken stoppingToken)
    {
        using var httpClient = _httpClientFactory.CreateClient();
        var client = new NukiBridgeClient(httpClient, hostAddress, token, TokenMode, BusyRetryDelay);
        try
        {
            StatusMessage = null;
            Status = ServiceStatus.Starting;
            await RefreshInfoAsync(client, stoppingToken);
            await RefreshListAsync(client, stoppingToken);
            LastUpdated = DateTimeOffset.UtcNow;

            _client = client;
            IsConnected = true;
            Status = ServiceStatus.Running;

            await RunPollingLoopAsync(client, stoppingToken);
            return true;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Logger.LogWarning(exception, "Nuki bridge {HostAddress} connection failed.", hostAddress);
            StatusMessage = exception.Message;
            Status = ServiceStatus.Error;
            return false;
        }
        finally
        {
            _client = null;
            IsConnected = false;

            // The HTTP client is disposed when this method returns, which cancels a request still in flight, such as
            // a lock action waiting for the motor. Before a reconnect, wait once for the gate so that the last request
            // finishes first; no new request takes the cleared client, and the request's own timeout bounds the wait.
            // A stop cancels the wait, also when it is already running: the motor finishes anyway.
            await _requestGate.WaitAsync(stoppingToken);
            _requestGate.Release();
        }
    }

    private async Task RunPollingLoopAsync(NukiBridgeClient client, CancellationToken stoppingToken)
    {
        var failures = 0;
        var poll = 0;
        while (!await _configurationChangedSignal.WaitAsync(GetEffectiveInterval(PollingInterval, DefaultPollingInterval), stoppingToken))
        {
            poll++;
            try
            {
                await RefreshListAsync(client, stoppingToken);
                if (poll % InfoPollDivider == 0)
                {
                    await RefreshInfoAsync(client, stoppingToken);
                }

                LastUpdated = DateTimeOffset.UtcNow;
                failures = 0;
                StatusMessage = null;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                failures++;
                if (failures >= MaxConsecutivePollFailures)
                {
                    throw;
                }

                StatusMessage = $"Poll failed ({failures} of {MaxConsecutivePollFailures}): {exception.Message}";
                Logger.LogWarning(
                    exception,
                    "Nuki bridge {HostAddress} poll failed ({FailureCount} of {MaxFailureCount}).",
                    HostAddress, failures, MaxConsecutivePollFailures);
            }
        }
    }

    /// <summary>
    /// Runs a lock action of a device and waits until the device has finished it. <paramref name="enterTransition"/>
    /// runs right before the request is sent. Once the request was sent, the device list is read whether the action
    /// succeeded or not; when that read fails or the connection has ended meanwhile, the state of the device becomes
    /// unknown (<see cref="NukiDevice.ResetActionState"/>) until the next device list.
    /// </summary>
    /// <exception cref="InvalidOperationException">The bridge is not connected and nothing was sent, or the device
    /// reported a failure, with <paramref name="failureMessage"/> as the message.</exception>
    /// <exception cref="OperationCanceledException">The caller canceled, possibly before anything was sent.</exception>
    internal async Task ExecuteActionAsync(
        NukiDevice device, int action, Action? enterTransition, string failureMessage, CancellationToken cancellationToken)
    {
        NukiActionResponse result;
        using (await EnterRequestGateAsync(cancellationToken))
        {
            // Read under the gate, so that an action never uses a client whose connection has ended meanwhile.
            var client = _client ?? throw new InvalidOperationException(
                StatusMessage is { } statusMessage ? "The bridge is not connected. " + statusMessage : "The bridge is not connected.");

            // Entered only while the gate is held, so that a poll that is still running cannot overwrite the transition
            // with the state from before the action.
            enterTransition?.Invoke();
            try
            {
                result = await client.LockActionAsync(device.NukiId, (int)device.DeviceType, action, cancellationToken);
            }
            finally
            {
                await RefreshAfterActionAsync(device, action);
            }
        }

        if (!result.Success)
        {
            throw new InvalidOperationException(failureMessage);
        }
    }

    // Runs while the gate of the action is still held, so that no poll reads the list in between, and a reconnect
    // reads the list only after it. The client is read again because the connection may have ended during the action.
    private async Task RefreshAfterActionAsync(NukiDevice device, int action)
    {
        if (_client is not { } client)
        {
            device.ResetActionState();
            Logger.LogDebug("Nuki device {NukiId} is not read after action {Action}: the connection has ended.", device.NukiId, action);
            return;
        }

        try
        {
            MergeDevices(await client.GetListAsync(CancellationToken.None));
            LastUpdated = DateTimeOffset.UtcNow;
        }
        catch (Exception exception)
        {
            device.ResetActionState();
            Logger.LogWarning(exception, "Nuki device {NukiId} could not be read after action {Action}.", device.NukiId, action);
        }
    }

    private async Task RefreshListAsync(NukiBridgeClient client, CancellationToken cancellationToken)
    {
        using (await EnterRequestGateAsync(cancellationToken))
        {
            MergeDevices(await client.GetListAsync(cancellationToken));
        }
    }

    private async Task RefreshInfoAsync(NukiBridgeClient client, CancellationToken cancellationToken)
    {
        using (await EnterRequestGateAsync(cancellationToken))
        {
            ApplyInfo(await client.GetInfoAsync(cancellationToken));
        }
    }

    private async Task<RequestGateLease> EnterRequestGateAsync(CancellationToken cancellationToken)
    {
        await _requestGate.WaitAsync(cancellationToken);
        return new RequestGateLease(_requestGate);
    }

    private readonly struct RequestGateLease(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }
}
