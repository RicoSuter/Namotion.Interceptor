using System.ComponentModel;
using System.Net.Http.Json;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Common;
using HomeBlaze.Abstractions.Devices;
using HomeBlaze.Abstractions.Networking;
using HomeBlaze.Abstractions.Sensors;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Devices.MyStrom.Model;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;

namespace Namotion.Devices.MyStrom;

[Category("Devices")]
[Description("myStrom WiFi Switch with power metering, temperature sensing, and relay control")]
[InterceptorSubject]
public partial class MyStromSwitch : BackgroundService,
    IConfigurable,
    IMonitoredService,
    ITitleProvider,
    IIconProvider,
    ILastUpdatedProvider,
    IConnectionState,
    IPowerRelay,
    IPowerMeter,
    ITemperatureSensor,
    INetworkAdapter
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<MyStromSwitch> _logger;
    private readonly SemaphoreSlim _configChangedSignal = new(0, 1);

    private MyStromSwitchInformation? _information;

    [Configuration]
    public partial string Name { get; set; }

    [Configuration]
    public partial string? HostAddress { get; set; }

    [Configuration]
    public partial bool AllowTurnOff { get; set; }

    [Configuration]
    public partial TimeSpan PollingInterval { get; set; }

    [Configuration]
    public partial TimeSpan RetryInterval { get; set; }

    [State(IsDiscrete = true)]
    public partial bool IsConnected { get; internal set; }

    [State(IsDiscrete = true)]
    public partial ServiceStatus Status { get; internal set; }

    [State]
    public partial string? StatusMessage { get; internal set; }

    [State]
    public partial DateTimeOffset? LastUpdated { get; internal set; }

    [State(IsDiscrete = true)]
    public partial bool? IsOn { get; internal set; }

    [State(Unit = StateUnit.Watt)]
    public partial decimal? MeasuredPower { get; internal set; }

    [State(Unit = StateUnit.WattHour, IsCumulative = true)]
    public partial decimal? MeasuredEnergyConsumed { get; internal set; }

    [State(Unit = StateUnit.DegreeCelsius)]
    public partial decimal? Temperature { get; internal set; }

    [State]
    public partial TimeSpan? Uptime { get; internal set; }

    [Derived]
    public string? Title => string.IsNullOrEmpty(Name) ? HostAddress : Name;

    [Derived]
    public string IconName => "Power";

    [Derived]
    public string? IconColor => IsOn switch
    {
        true => "Success",
        false => "Error",
        _ => null
    };

    [Derived]
    [State]
    public string? MacAddress => _information?.Mac;

    [Derived]
    [State]
    public string? IpAddress => _information?.Ip;

    [Derived]
    [State]
    public string? SubnetMask => _information?.Mask;

    [Derived]
    [State]
    public string? Gateway => _information?.Gateway;

    [Derived]
    [State]
    public string? DeviceType => _information?.Type;

    [Derived]
    [State]
    public string? FirmwareVersion => _information?.Version;

    public bool? IsWireless => true;
    public int? SignalStrength => null;

    [Derived]
    [PropertyAttribute("TurnOn", KnownAttributes.IsEnabled)]
    public bool TurnOn_IsEnabled => IsConnected && IsOn != true;

    [Derived]
    [PropertyAttribute("TurnOff", KnownAttributes.IsEnabled)]
    public bool TurnOff_IsEnabled => IsConnected && IsOn == true && AllowTurnOff;

    public MyStromSwitch(IHttpClientFactory httpClientFactory, ILogger<MyStromSwitch> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;

        Name = string.Empty;
        HostAddress = null;
        AllowTurnOff = true;
        PollingInterval = TimeSpan.FromSeconds(15);
        RetryInterval = TimeSpan.FromSeconds(30);

        IsConnected = false;
        Status = ServiceStatus.Stopped;
        StatusMessage = null;
        LastUpdated = null;
        IsOn = null;
        MeasuredPower = null;
        MeasuredEnergyConsumed = null;
        Temperature = null;
        Uptime = null;
    }

    [Operation(Title = "Turn On", Icon = "PowerSettingsNew", Position = 1)]
    public async Task TurnOnAsync(CancellationToken cancellationToken)
    {
        using var client = _httpClientFactory.CreateClient();
        await client.GetAsync($"http://{HostAddress}/relay?state=1", cancellationToken);
        await RefreshReportAsync(client, cancellationToken);
    }

    [Operation(Title = "Turn Off", Icon = "PowerOff", Position = 2)]
    public async Task TurnOffAsync(CancellationToken cancellationToken)
    {
        if (!AllowTurnOff)
            return;

        using var client = _httpClientFactory.CreateClient();
        await client.GetAsync($"http://{HostAddress}/relay?state=0", cancellationToken);
        await RefreshReportAsync(client, cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (string.IsNullOrEmpty(HostAddress))
            {
                Status = ServiceStatus.Stopped;
                StatusMessage = "No IP address configured";
                try
                {
                    await _configChangedSignal.WaitAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                continue;
            }

            try
            {
                Status = ServiceStatus.Starting;
                StatusMessage = "Connecting...";

                using var client = _httpClientFactory.CreateClient();
                await FetchInformationAsync(client, stoppingToken);

                Status = ServiceStatus.Running;
                StatusMessage = null;
                IsConnected = true;

                await RunPollingLoopAsync(client, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "MyStrom switch {HostAddress} connection failed", HostAddress);
                IsConnected = false;
                Status = ServiceStatus.Error;
                StatusMessage = exception.Message;
                IsOn = null;
                MeasuredPower = null;
                Temperature = null;

                await Task.Delay(RetryInterval, stoppingToken);
            }
        }

        Status = ServiceStatus.Stopped;
        StatusMessage = null;
    }

    private async Task RunPollingLoopAsync(HttpClient client, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RefreshReportAsync(client, stoppingToken);
                await RefreshTemperatureAsync(client, stoppingToken);
                LastUpdated = DateTimeOffset.UtcNow;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "MyStrom switch {HostAddress} poll failed", HostAddress);
                IsConnected = false;
                Status = ServiceStatus.Error;
                StatusMessage = exception.Message;
                return; // Exit polling loop to trigger reconnect
            }

            try
            {
                var signaled = await _configChangedSignal.WaitAsync(PollingInterval, stoppingToken);
                if (signaled)
                {
                    _information = null;
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
        var response = await client.GetAsync($"http://{HostAddress}/info", cancellationToken);
        response.EnsureSuccessStatusCode();

        _information = await response.Content.ReadFromJsonAsync<MyStromSwitchInformation>(cancellationToken);
    }

    private async Task RefreshReportAsync(HttpClient client, CancellationToken cancellationToken)
    {
        var response = await client.GetAsync($"http://{HostAddress}/report", cancellationToken);
        response.EnsureSuccessStatusCode();

        var report = await response.Content.ReadFromJsonAsync<MyStromSwitchReport>(cancellationToken);
        if (report != null)
        {
            IsOn = report.Relay;
            MeasuredPower = Math.Round(report.Power, 1);
            MeasuredEnergyConsumed = Math.Round(report.EnergySinceBoot / 3600m, 2);
            Uptime = TimeSpan.FromSeconds(report.TimeSinceBoot);
            IsConnected = true;
        }
    }

    private async Task RefreshTemperatureAsync(HttpClient client, CancellationToken cancellationToken)
    {
        var response = await client.GetAsync($"http://{HostAddress}/api/v1/temperature", cancellationToken);
        response.EnsureSuccessStatusCode();

        var temperature = await response.Content.ReadFromJsonAsync<MyStromSwitchTemperature>(cancellationToken);
        if (temperature != null)
        {
            Temperature = Math.Round(temperature.Compensated, 2);
        }
    }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default)
    {
        if (_configChangedSignal.CurrentCount == 0)
        {
            _configChangedSignal.Release();
        }

        return Task.CompletedTask;
    }
}
