using System.Net.Http.Json;
using HomeBlaze.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Devices.MyStrom.Model;

namespace Namotion.Devices.MyStrom;

/// <summary>Polls a myStrom switch over HTTP and runs its relay operations.</summary>
internal sealed class MyStromPoller : BackgroundService
{
    private readonly MyStromSwitch _device;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly ILogger _logger;

    public MyStromPoller(MyStromSwitch device, IHttpClientFactory? httpClientFactory, ILogger<MyStromPoller>? logger)
    {
        _device = device;
        _httpClientFactory = httpClientFactory;
        _logger = logger ?? (ILogger)NullLogger.Instance;
    }

    public async Task SetRelayAsync(bool isOn, CancellationToken cancellationToken)
    {
        using var client = CreateClient();
        await client.GetAsync($"http://{_device.HostAddress}/relay?state={(isOn ? 1 : 0)}", cancellationToken);
        await RefreshReportAsync(client, cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _device.SetActiveService(this);
        try
        {
            await RunAsync(stoppingToken);
        }
        finally
        {
            _device.ClearActiveService(this);
        }
    }

    private HttpClient CreateClient() => _httpClientFactory?.CreateClient() ?? new HttpClient();

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (string.IsNullOrEmpty(_device.HostAddress))
            {
                _device.Status = ServiceStatus.Stopped;
                _device.StatusMessage = "No IP address configured";
                try
                {
                    await _device.ConfigurationChanged.WaitAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                continue;
            }

            try
            {
                _device.Status = ServiceStatus.Starting;
                _device.StatusMessage = "Connecting...";

                using var client = CreateClient();
                await FetchInformationAsync(client, stoppingToken);

                _device.Status = ServiceStatus.Running;
                _device.StatusMessage = null;
                _device.IsConnected = true;

                await RunPollingLoopAsync(client, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "MyStrom switch {HostAddress} connection failed", _device.HostAddress);
                _device.IsConnected = false;
                _device.Status = ServiceStatus.Error;
                _device.StatusMessage = exception.Message;
                _device.IsOn = null;
                _device.MeasuredPower = null;
                _device.Temperature = null;

                try
                {
                    await Task.Delay(_device.RetryInterval, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        _device.IsConnected = false;
        _device.Status = ServiceStatus.Stopped;
        _device.StatusMessage = null;
    }

    private async Task RunPollingLoopAsync(HttpClient client, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RefreshReportAsync(client, stoppingToken);
                await RefreshTemperatureAsync(client, stoppingToken);
                _device.LastUpdated = DateTimeOffset.UtcNow;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "MyStrom switch {HostAddress} poll failed", _device.HostAddress);
                _device.IsConnected = false;
                _device.Status = ServiceStatus.Error;
                _device.StatusMessage = exception.Message;
                return; // Exit polling loop to trigger reconnect
            }

            try
            {
                var signaled = await _device.ConfigurationChanged.WaitAsync(_device.PollingInterval, stoppingToken);
                if (signaled)
                {
                    _device.Information = null;
                    return; // Exit polling loop to reinitialize
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task FetchInformationAsync(HttpClient client, CancellationToken cancellationToken)
    {
        var response = await client.GetAsync($"http://{_device.HostAddress}/info", cancellationToken);
        response.EnsureSuccessStatusCode();

        _device.Information = await response.Content.ReadFromJsonAsync<MyStromSwitchInformation>(cancellationToken);
    }

    private async Task RefreshReportAsync(HttpClient client, CancellationToken cancellationToken)
    {
        var response = await client.GetAsync($"http://{_device.HostAddress}/report", cancellationToken);
        response.EnsureSuccessStatusCode();

        var report = await response.Content.ReadFromJsonAsync<MyStromSwitchReport>(cancellationToken);
        if (report != null)
        {
            _device.IsOn = report.IsRelayOn;
            _device.MeasuredPower = Math.Round(report.Power, 1);
            _device.MeasuredEnergyConsumed = Math.Round(report.EnergySinceBoot / 3600m, 2);
            _device.Uptime = TimeSpan.FromSeconds(report.TimeSinceBoot);
            _device.IsConnected = true;
        }
    }

    private async Task RefreshTemperatureAsync(HttpClient client, CancellationToken cancellationToken)
    {
        var response = await client.GetAsync($"http://{_device.HostAddress}/api/v1/temperature", cancellationToken);
        response.EnsureSuccessStatusCode();

        var temperature = await response.Content.ReadFromJsonAsync<MyStromSwitchTemperature>(cancellationToken);
        if (temperature != null)
        {
            _device.Temperature = Math.Round(temperature.Compensated, 2);
        }
    }
}
