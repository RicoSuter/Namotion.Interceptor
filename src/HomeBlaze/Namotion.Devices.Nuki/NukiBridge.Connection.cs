using HomeBlaze.Abstractions;
using Microsoft.Extensions.Logging;
using Namotion.Devices.Nuki.Client;

namespace Namotion.Devices.Nuki;

public partial class NukiBridge
{
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
            // Normal shutdown: StopAsync canceled stoppingToken, so the loop above exits here.
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

    private static TimeSpan GetEffectiveInterval(TimeSpan configured, TimeSpan fallback) =>
        configured > TimeSpan.Zero ? configured : fallback;
}
