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
    public static TimeSpan GetEffectiveInterval(TimeSpan configured, TimeSpan fallback)
    {
        if (configured <= TimeSpan.Zero)
        {
            return fallback;
        }

        var minimum = TimeSpan.FromSeconds(MinimumIntervalSeconds);
        if (configured < minimum)
        {
            return minimum;
        }

        var maximum = TimeSpan.FromSeconds(MaximumIntervalSeconds);
        return configured > maximum ? maximum : configured;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (true)
            {
                stoppingToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(HostAddress) || string.IsNullOrEmpty(Token))
                {
                    Status = ServiceStatus.Error;
                    StatusMessage = "Host address and token are required.";
                    await _configurationChangedSignal.WaitAsync(stoppingToken);
                    continue;
                }

                if (!await ConnectAndPollAsync(HostAddress, Token, stoppingToken))
                {
                    await _configurationChangedSignal.WaitAsync(GetEffectiveInterval(RetryInterval, DefaultRetryInterval), stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopped.
        }

        Status = ServiceStatus.Stopped;
        StatusMessage = null;
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
            Status = ServiceStatus.Starting;
            StatusMessage = null;
            await RefreshInfoAsync(client, stoppingToken);

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
            Status = ServiceStatus.Error;
            StatusMessage = exception.Message;
            return false;
        }
        finally
        {
            _client = null;
            IsConnected = false;
        }
    }

    private async Task RunPollingLoopAsync(NukiBridgeClient client, CancellationToken stoppingToken)
    {
        var failures = 0;
        var poll = 1;
        while (true)
        {
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

            if (await _configurationChangedSignal.WaitAsync(GetEffectiveInterval(PollingInterval, DefaultPollingInterval), stoppingToken))
            {
                return;
            }

            poll++;
        }
    }

    internal void ThrowIfNotConnected() => GetConnectedClient();

    /// <summary>
    /// Runs a lock action of a device and returns the result the bridge reports once the device has finished it.
    /// <paramref name="enterTransition"/> runs right before the request is sent.
    /// </summary>
    internal async Task<NukiActionResponse> ExecuteActionAsync(
        NukiDevice device, int action, Action? enterTransition, CancellationToken cancellationToken)
    {
        var client = GetConnectedClient();
        using (await EnterRequestGateAsync(cancellationToken))
        {
            // Entered only while the gate is held, so that a poll that is still running cannot overwrite the transition
            // with the state from before the action.
            enterTransition?.Invoke();
            return await client.LockActionAsync(device.NukiId, (int)device.DeviceType, action, cancellationToken);
        }
    }

    /// <summary>
    /// Reads the device list and updates the devices.
    /// </summary>
    internal async Task RefreshAsync(CancellationToken cancellationToken)
    {
        await RefreshListAsync(GetConnectedClient(), cancellationToken);
        LastUpdated = DateTimeOffset.UtcNow;
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

    private NukiBridgeClient GetConnectedClient() =>
        _client ?? throw new InvalidOperationException(
            StatusMessage is { } statusMessage ? "The bridge is not connected. " + statusMessage : "The bridge is not connected.");

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
