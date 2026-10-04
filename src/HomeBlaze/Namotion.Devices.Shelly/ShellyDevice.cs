using System.ComponentModel;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Common;
using HomeBlaze.Abstractions.Devices;
using HomeBlaze.Abstractions.Networking;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Devices.Shelly.Model;
using Namotion.Interceptor.Attributes;

namespace Namotion.Devices.Shelly;

[Category("Devices")]
[Description("Shelly Gen2 device with dynamic component discovery (switches, covers, energy meters, inputs, temperature sensors, phase netted energy counters)")]
[InterceptorSubject]
public partial class ShellyDevice : BackgroundService,
    IConfigurable,
    IMonitoredService,
    ITitleProvider,
    IIconProvider,
    ILastUpdatedProvider,
    IConnectionState,
    INetworkAdapter,
    ISoftwareState,
    IDeviceInfo
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ShellyDevice> _logger;
    private readonly SemaphoreSlim _configChangedSignal = new(0, 1);
    private readonly Lock _updateLock = new();
    private readonly ShellyVirtualEnergyCounters _virtualEnergyCounters = new();

    private ShellyDeviceInfo? _deviceInfo;
    private string? _ethernetIpAddress;
    private string? _wifiIpAddress;
    private int? _wifiSignalStrength;
    private string? _availableSoftwareUpdate;
    
    [Configuration]
    public partial string Name { get; set; }

    [Configuration]
    public partial string? HostAddress { get; set; }

    [Configuration(IsSecret = true)]
    public partial string? Password { get; set; }

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

    [State(Position = 950)]
    public partial DateTimeOffset? LastUpdated { get; internal set; }

    [State]
    public partial ShellySwitch[] Switches { get; internal set; }

    [State]
    public partial ShellyCover[] Covers { get; internal set; }

    [State]
    public partial ShellyInput[] Inputs { get; internal set; }

    [State]
    public partial ShellyTemperatureSensor[] TemperatureSensors { get; internal set; }

    [State]
    public partial ShellyEnergyMeter? EnergyMeter { get; internal set; }

    [State(Position = 402)]
    public partial TimeSpan? Uptime { get; internal set; }
    
    [Derived]
    [State]
    public string? SoftwareVersion => _deviceInfo?.Version;

    [Derived]
    [State]
    public string? AvailableSoftwareUpdate => _availableSoftwareUpdate;

    [Derived]
    [State]
    public string? MacAddress => _deviceInfo?.Mac;

    [Derived]
    [State]
    public string? IpAddress => _ethernetIpAddress ?? _wifiIpAddress;

    public string? SubnetMask => null;
    public string? Gateway => null;

    [Derived]
    [State]
    public bool? IsWireless =>
        _ethernetIpAddress != null ? false :
        _wifiIpAddress != null ? true : null;

    [Derived]
    [State]
    public int? SignalStrength => IsWireless == true ? _wifiSignalStrength : null;
    
    [Derived]
    public string? Title => !string.IsNullOrEmpty(Name) ? Name :
        _deviceInfo?.Name ?? _deviceInfo?.Application ?? HostAddress;

    [Derived]
    public string IconName => IsConnected ? "Hub" : "HubOutlined";

    [Derived]
    public string? IconColor => IsConnected ? "Success" : null;
    
    [Derived]
    [State(Position = 400)]
    public string? DeviceName => _deviceInfo?.Name;

    // IDeviceInfo

    [Derived]
    [State]
    public string? Manufacturer => "Shelly";

    [Derived]
    [State]
    public string? Model => _deviceInfo?.Model;

    [Derived]
    [State]
    public string? ProductCode => _deviceInfo?.Application;

    [Derived]
    [State]
    public string? SerialNumber => _deviceInfo?.Mac;

    [Derived]
    [State]
    public string? HardwareRevision => _deviceInfo?.Generation?.ToString();

    [Derived]
    [State(Position = 401)]
    public int? Generation => _deviceInfo?.Generation;

    public ShellyDevice(IHttpClientFactory httpClientFactory, ILogger<ShellyDevice> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;

        Name = string.Empty;
        HostAddress = null;
        Password = null;
        PollingInterval = TimeSpan.FromSeconds(15);
        RetryInterval = TimeSpan.FromSeconds(30);

        IsConnected = false;
        Status = ServiceStatus.Stopped;
        StatusMessage = null;
        LastUpdated = null;
        Switches = [];
        Covers = [];
        Inputs = [];
        TemperatureSensors = [];
        EnergyMeter = null;
        Uptime = null;
    }

    internal HttpClient CreateHttpClient()
    {
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(10);
        return client;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (string.IsNullOrEmpty(HostAddress))
            {
                Status = ServiceStatus.Stopped;
                StatusMessage = "No host address configured";
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

                using var client = CreateHttpClient();
                await FetchDeviceInfoAsync(client, stoppingToken);

                if (_deviceInfo?.Generation < 2)
                {
                    Status = ServiceStatus.Error;
                    StatusMessage = "Only Gen2+ Shelly devices are supported";
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

                Status = ServiceStatus.Running;
                StatusMessage = null;
                IsConnected = true;

                // Start WebSocket as parallel task alongside polling
                var webSocketClient = new ShellyWebSocketClient(this, _logger);
                var webSocketCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var webSocketTask = webSocketClient.RunAsync(webSocketCts.Token);

                var isConfigurationChanged = false;
                try
                {
                    isConfigurationChanged = await RunPollingLoopAsync(client, stoppingToken);
                }
                finally
                {
                    await webSocketCts.CancelAsync();
                    try { await webSocketTask; } catch (OperationCanceledException) { }
                    webSocketClient.Dispose();
                    webSocketCts.Dispose();
                }

                if (isConfigurationChanged)
                    ResetForConfigurationChange();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Shelly device {HostAddress} connection failed", HostAddress);
                IsConnected = false;
                Status = ServiceStatus.Error;
                StatusMessage = exception.Message;
                ResetState();

                await Task.Delay(RetryInterval, stoppingToken);
            }
        }

        Status = ServiceStatus.Stopped;
        StatusMessage = null;
    }

    /// <returns><c>true</c> when the loop stopped because the configuration changed.</returns>
    private async Task<bool> RunPollingLoopAsync(HttpClient client, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollStatusAsync(client, stoppingToken);
                await ReadVirtualEnergyCountersAsync(client, stoppingToken);
                LastUpdated = DateTimeOffset.UtcNow;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Shelly device {HostAddress} poll failed", HostAddress);
                IsConnected = false;
                Status = ServiceStatus.Error;
                StatusMessage = exception.Message;
                return false;
            }

            var pollingInterval = Covers.Any(c => c.IsMoving == true)
                ? TimeSpan.FromSeconds(1)
                : PollingInterval;

            try
            {
                var signaled = await _configChangedSignal.WaitAsync(pollingInterval, stoppingToken);
                if (signaled)
                    return true;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        return false;
    }

    private async Task FetchDeviceInfoAsync(HttpClient client, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync($"http://{HostAddress}/shelly", cancellationToken);
        response.EnsureSuccessStatusCode();
        _deviceInfo = await response.Content.ReadFromJsonAsync<ShellyDeviceInfo>(cancellationToken);
    }

    private async Task PollStatusAsync(HttpClient client, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync($"http://{HostAddress}/rpc/Shelly.GetStatus", cancellationToken);
        response.EnsureSuccessStatusCode();
        var statusJson = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);

        ParseStatusComponents(statusJson);

        IsConnected = true;
    }

    /// <summary>
    /// Reads the names and values of the virtual energy counters when the device has an energy meter and a read is required.
    /// </summary>
    internal async Task ReadVirtualEnergyCountersAsync(HttpClient client, CancellationToken cancellationToken)
    {
        ShellyEnergyMeter? energyMeter;
        lock (_updateLock)
        {
            energyMeter = EnergyMeter;
            if (energyMeter == null || !_virtualEnergyCounters.IsReadRequired)
                return;
        }

        try
        {
            var numbers = new List<ShellyVirtualNumber>();

            // The first page's revision is recorded: if the configuration changes while paging, the newer revision
            // observed in the next status differs from it and triggers another read.
            var (offset, total, configurationRevision) = await ReadVirtualNumbersPageAsync(client, 0, numbers, cancellationToken);
            while (offset < total)
            {
                var requestedOffset = offset;
                (offset, total, _) = await ReadVirtualNumbersPageAsync(client, offset, numbers, cancellationToken);

                // Requiring progress stops a device that ignores the offset parameter from repeating the same page forever.
                if (offset <= requestedOffset)
                    break;
            }

            lock (_updateLock)
            {
                _virtualEnergyCounters.Apply(numbers, configurationRevision, energyMeter);
            }
        }
        catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            // Firmware without Shelly.GetComponents; reads wait for the next configuration change, so this is logged once per revision.
            _logger.LogWarning(exception, "Shelly device {HostAddress} does not support virtual components, retrying after the next configuration change", HostAddress);
            lock (_updateLock)
            {
                _virtualEnergyCounters.ApplyUnsupported(energyMeter);
            }
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug(exception, "Shelly device {HostAddress} virtual components could not be read, retrying on next poll", HostAddress);
        }
    }

    private async Task<(int NextOffset, int Total, int? ConfigurationRevision)> ReadVirtualNumbersPageAsync(
        HttpClient client, int offset, List<ShellyVirtualNumber> numbers, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(
            $"http://{HostAddress}/rpc/Shelly.GetComponents?dynamic_only=true&include=%5B%22config%22,%22status%22%5D&offset={offset}",
            cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return ShellyVirtualEnergyCounters.ReadVirtualNumbers(document.RootElement, numbers);
    }

    internal void ParseStatusComponents(JsonElement root, bool isPartialUpdate = false)
    {
        lock (_updateLock)
        {
            ParseStatusComponentsCore(root, isPartialUpdate);
        }
    }

    private void ParseStatusComponentsCore(JsonElement root, bool isPartialUpdate)
    {
        List<(int index, ShellySwitchStatus status)>? switches = null;
        List<(int index, ShellyCoverStatus status)>? covers = null;
        List<(int index, ShellyInputStatus status)>? inputs = null;
        List<(int index, ShellyTemperatureStatus status)>? temperatures = null;
        ShellyEmStatus? emStatus = null;
        ShellyEmDataStatus? emDataStatus = null;

        foreach (var property in root.EnumerateObject())
        {
            var (componentType, index) = ParseComponentKey(property.Name);
            switch (componentType)
            {
                case "switch":
                    var switchStatus = property.Value.Deserialize<ShellySwitchStatus>();
                    if (switchStatus != null)
                        (switches ??= []).Add((index, switchStatus));
                    break;

                case "cover":
                    var coverStatus = property.Value.Deserialize<ShellyCoverStatus>();
                    if (coverStatus != null)
                        (covers ??= []).Add((index, coverStatus));
                    break;

                case "input":
                    var inputStatus = property.Value.Deserialize<ShellyInputStatus>();
                    if (inputStatus != null)
                        (inputs ??= []).Add((index, inputStatus));
                    break;

                case "temperature":
                    var tempStatus = property.Value.Deserialize<ShellyTemperatureStatus>();
                    if (tempStatus != null)
                        (temperatures ??= []).Add((index, tempStatus));
                    break;

                case "em":
                    emStatus = property.Value.Deserialize<ShellyEmStatus>();
                    break;

                case "emdata":
                    emDataStatus = property.Value.Deserialize<ShellyEmDataStatus>();
                    break;

                case "sys":
                    var sysStatus = property.Value.Deserialize<ShellySysStatus>();
                    if (sysStatus != null)
                        UpdateSysStatus(sysStatus);
                    break;

                // Pushes contain only the changed fields (e.g. rssi alone), so a missing field keeps its value.
                case "eth":
                    if (property.Value.TryGetProperty("ip", out var ethernetIp))
                        _ethernetIpAddress = GetStringOrNull(ethernetIp);
                    break;

                case "wifi":
                    if (property.Value.TryGetProperty("sta_ip", out var wifiIp))
                        _wifiIpAddress = GetStringOrNull(wifiIp);
                    if (property.Value.TryGetProperty("rssi", out var rssi))
                        _wifiSignalStrength = rssi.ValueKind == JsonValueKind.Number ? rssi.GetInt32() : null;
                    break;
            }
        }

        // Only update component types that were actually present in the response.
        // WebSocket NotifyStatus messages contain only changed components —
        // calling UpdateX with an empty list would wipe existing children.
        if (switches != null)
            UpdateSwitches(switches, isPartialUpdate);
        if (covers != null)
            UpdateCovers(covers, isPartialUpdate);
        if (inputs != null)
            UpdateInputs(inputs, isPartialUpdate);
        if (temperatures != null)
            UpdateTemperatureSensors(temperatures, isPartialUpdate);
        if (emStatus != null || emDataStatus != null)
            UpdateEnergyMeter(emStatus, emDataStatus);
    }

    private static string? GetStringOrNull(JsonElement element) =>
        element.ValueKind == JsonValueKind.String ? element.GetString() : null;

    internal static (string componentType, int index) ParseComponentKey(string key)
    {
        var colonIndex = key.IndexOf(':');
        if (colonIndex < 0)
            return (key, 0);

        var componentType = key[..colonIndex];
        if (int.TryParse(key[(colonIndex + 1)..], out var index))
            return (componentType, index);

        return (key, 0);
    }

    private void UpdateSwitches(List<(int index, ShellySwitchStatus status)> switchData, bool isPartialUpdate)
    {
        if (switchData.Count == 0 && Switches.Length == 0)
            return;

        if (!isPartialUpdate)
        {
            var ordered = switchData.OrderBy(s => s.index).ToList();

            if (Switches.Length != ordered.Count)
            {
                foreach (var oldSwitch in Switches)
                    oldSwitch.Dispose();
                Switches = ordered.Select(s => new ShellySwitch(this, s.index)).ToArray();
            }

            for (var i = 0; i < ordered.Count; i++)
            {
                var status = ordered[i].status;
                var previousIsOn = Switches[i].IsOn;

                Switches[i].IsOn = status.IsOutputOn;
                Switches[i].Source = status.Source;
                Switches[i].MeasuredPower = status.ActivePower;
                Switches[i].TotalImportedEnergy = status.ActiveEnergy?.Total;
                Switches[i].TotalExportedEnergy = status.ReturnedActiveEnergy?.Total;
                Switches[i].ElectricalVoltage = status.Voltage;
                Switches[i].ElectricalCurrent = status.Current;
                Switches[i].Temperature = status.Temperature?.TemperatureCelsius;
                Switches[i].LastUpdated = DateTimeOffset.UtcNow;

                if (previousIsOn != status.IsOutputOn && status.IsOutputOn != null)
                    Switches[i].PublishSwitchEvent();
            }
        }
        else
        {
            foreach (var (componentIndex, status) in switchData)
            {
                var sw = Switches.FirstOrDefault(s => s.Index == componentIndex);
                if (sw == null) continue;

                var previousIsOn = sw.IsOn;
                if (status.IsOutputOn != null) sw.IsOn = status.IsOutputOn;
                if (status.Source != null) sw.Source = status.Source;
                if (status.ActivePower != null) sw.MeasuredPower = status.ActivePower;
                if (status.ActiveEnergy != null) sw.TotalImportedEnergy = status.ActiveEnergy.Total;
                if (status.ReturnedActiveEnergy != null) sw.TotalExportedEnergy = status.ReturnedActiveEnergy.Total;
                if (status.Voltage != null) sw.ElectricalVoltage = status.Voltage;
                if (status.Current != null) sw.ElectricalCurrent = status.Current;
                if (status.Temperature != null) sw.Temperature = status.Temperature.TemperatureCelsius;
                sw.LastUpdated = DateTimeOffset.UtcNow;

                if (previousIsOn != sw.IsOn && sw.IsOn != null)
                    sw.PublishSwitchEvent();
            }
        }
    }

    private void UpdateCovers(List<(int index, ShellyCoverStatus status)> coverData, bool isPartialUpdate)
    {
        if (coverData.Count == 0 && Covers.Length == 0)
            return;

        if (!isPartialUpdate)
        {
            var ordered = coverData.OrderBy(c => c.index).ToList();

            if (Covers.Length != ordered.Count)
            {
                Covers = ordered.Select(c => new ShellyCover(this, c.index)).ToArray();
            }

            for (var i = 0; i < ordered.Count; i++)
            {
                var status = ordered[i].status;
                Covers[i].MeasuredPower = status.ActivePower;
                Covers[i].TotalImportedEnergy = status.ActiveEnergy?.Total;
                Covers[i].ElectricalVoltage = status.Voltage;
                Covers[i].ElectricalCurrent = status.Current;
                Covers[i].ElectricalFrequency = status.Frequency;
                Covers[i].PowerFactor = status.PowerFactor;
                Covers[i].Temperature = status.Temperature?.TemperatureCelsius;
                Covers[i].Source = status.Source;
                Covers[i].LastDirection = status.LastDirection;
                Covers[i].CurrentPosition = status.CurrentPosition;
                Covers[i].ApiState = status.State;
                Covers[i].IsCalibrating = status.HasPositionControl == false;
                Covers[i].LastUpdated = DateTimeOffset.UtcNow;
            }
        }
        else
        {
            foreach (var (componentIndex, status) in coverData)
            {
                var cover = Covers.FirstOrDefault(c => c.Index == componentIndex);
                if (cover == null) continue;

                if (status.ActivePower != null) cover.MeasuredPower = status.ActivePower;
                if (status.ActiveEnergy != null) cover.TotalImportedEnergy = status.ActiveEnergy.Total;
                if (status.Voltage != null) cover.ElectricalVoltage = status.Voltage;
                if (status.Current != null) cover.ElectricalCurrent = status.Current;
                if (status.Frequency != null) cover.ElectricalFrequency = status.Frequency;
                if (status.PowerFactor != null) cover.PowerFactor = status.PowerFactor;
                if (status.Temperature != null) cover.Temperature = status.Temperature.TemperatureCelsius;
                if (status.Source != null) cover.Source = status.Source;
                if (status.LastDirection != null) cover.LastDirection = status.LastDirection;
                if (status.CurrentPosition != null) cover.CurrentPosition = status.CurrentPosition;
                if (status.State != null) cover.ApiState = status.State;
                if (status.HasPositionControl != null) cover.IsCalibrating = status.HasPositionControl == false;
                cover.LastUpdated = DateTimeOffset.UtcNow;
            }
        }
    }

    private void UpdateInputs(List<(int index, ShellyInputStatus status)> inputData, bool isPartialUpdate)
    {
        if (inputData.Count == 0 && Inputs.Length == 0)
            return;

        if (!isPartialUpdate)
        {
            var ordered = inputData.OrderBy(i => i.index).ToList();

            if (Inputs.Length != ordered.Count)
            {
                Inputs = ordered.Select(i => new ShellyInput(i.index)).ToArray();
            }

            for (var i = 0; i < ordered.Count; i++)
            {
                var status = ordered[i].status;
                Inputs[i].IsActive = status.IsActive;
                Inputs[i].TotalCount = status.Counts?.Total;
                Inputs[i].CountFrequency = status.Frequency;
                Inputs[i].LastUpdated = DateTimeOffset.UtcNow;
            }
        }
        else
        {
            foreach (var (componentIndex, status) in inputData)
            {
                var input = Inputs.FirstOrDefault(inp => inp.Index == componentIndex);
                if (input == null) continue;

                if (status.IsActive != null) input.IsActive = status.IsActive;
                if (status.Counts != null) input.TotalCount = status.Counts.Total;
                if (status.Frequency != null) input.CountFrequency = status.Frequency;
                input.LastUpdated = DateTimeOffset.UtcNow;
            }
        }
    }

    private void UpdateTemperatureSensors(List<(int index, ShellyTemperatureStatus status)> tempData, bool isPartialUpdate)
    {
        if (tempData.Count == 0 && TemperatureSensors.Length == 0)
            return;

        if (!isPartialUpdate)
        {
            var ordered = tempData.OrderBy(t => t.index).ToList();

            if (TemperatureSensors.Length != ordered.Count)
            {
                TemperatureSensors = ordered.Select(t => new ShellyTemperatureSensor(t.index)).ToArray();
            }

            for (var i = 0; i < ordered.Count; i++)
            {
                TemperatureSensors[i].Temperature = ordered[i].status.TemperatureCelsius;
                TemperatureSensors[i].LastUpdated = DateTimeOffset.UtcNow;
            }
        }
        else
        {
            foreach (var (componentIndex, status) in tempData)
            {
                var sensor = TemperatureSensors.FirstOrDefault(s => s.Index == componentIndex);
                if (sensor == null) continue;

                if (status.TemperatureCelsius != null) sensor.Temperature = status.TemperatureCelsius;
                sensor.LastUpdated = DateTimeOffset.UtcNow;
            }
        }
    }

    private void UpdateEnergyMeter(ShellyEmStatus? emStatus, ShellyEmDataStatus? emDataStatus)
    {
        // Only em:0 creates the meter; the device pushes emdata:0 on its own once per minute.
        if (emStatus != null)
        {
            EnergyMeter ??= new ShellyEnergyMeter();
            EnergyMeter.UpdateFromStatus(emStatus);
        }

        if (emDataStatus != null)
            EnergyMeter?.UpdateFromDataStatus(emDataStatus);
    }

    private void UpdateSysStatus(ShellySysStatus sysStatus)
    {
        if (sysStatus.Uptime != null)
            Uptime = TimeSpan.FromSeconds(sysStatus.Uptime.Value);

        if (sysStatus.ConfigurationRevision is { } configurationRevision)
            _virtualEnergyCounters.ObserveConfigurationRevision(configurationRevision);

        // Partial updates omit available_updates; a full status always contains it, possibly empty.
        if (sysStatus.AvailableUpdates != null)
            _availableSoftwareUpdate = sysStatus.AvailableUpdates.Stable?.Version;
    }

    /// <summary>
    /// Clears all device state, so the next connection starts like a new device.
    /// </summary>
    internal void ResetForConfigurationChange()
    {
        lock (_updateLock)
        {
            _deviceInfo = null;
            _ethernetIpAddress = null;
            _wifiIpAddress = null;
            _wifiSignalStrength = null;
        }

        ResetState();
    }

    private void ResetState()
    {
        lock (_updateLock)
        {
            foreach (var sw in Switches)
                sw.Dispose();

            Switches = [];
            Covers = [];
            Inputs = [];
            TemperatureSensors = [];
            EnergyMeter = null;
            Uptime = null;
            _virtualEnergyCounters.Reset();
        }
    }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default)
    {
        try { _configChangedSignal.Release(); }
        catch (SemaphoreFullException) { }

        return Task.CompletedTask;
    }
}
