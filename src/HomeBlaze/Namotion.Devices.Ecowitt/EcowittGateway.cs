using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Common;
using HomeBlaze.Abstractions.Devices;
using HomeBlaze.Abstractions.Networking;
using HomeBlaze.Storage.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Devices.Ecowitt.Models;
using Namotion.Devices.Ecowitt.Sensors;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Tracking.Parent;

namespace Namotion.Devices.Ecowitt;

[Category("Devices")]
[Description("Ecowitt weather station gateway with dynamic sensor discovery")]
[InterceptorSubject]
public partial class EcowittGateway : BackgroundService,
    IConfigurable,
    IMonitoredService,
    ITitleProvider,
    IIconProvider,
    ILastUpdatedProvider,
    IConnectionState,
    INetworkAdapter,
    ISoftwareState,
    IDeviceInfo,
    IHubDevice
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<EcowittGateway> _logger;
    private readonly SemaphoreSlim _configChangedSignal = new(0, 1);

    private EcowittVersionInfo? _versionInfo;
    private EcowittNetworkInfo? _networkInfo;
    private EcowittDeviceInfo? _deviceInfo;
    private EcowittSensorInfo[]? _sensorsInfo;

    [Configuration]
    public partial string Name { get; set; }

    [Configuration]
    public partial string? HostAddress { get; set; }

    [Configuration]
    public partial TimeSpan PollingInterval { get; set; }

    [Configuration]
    public partial TimeSpan RetryInterval { get; set; }

    [Configuration]
    public partial string[] HiddenSensors { get; set; }

    [Configuration]
    public partial decimal RainCumulativeOffset { get; set; }

    [Configuration]
    public partial decimal RainLastMonthlyValue { get; set; }

    [Configuration]
    public partial decimal PiezoRainCumulativeOffset { get; set; }

    [Configuration]
    public partial decimal PiezoRainLastMonthlyValue { get; set; }

    [State(IsDiscrete = true)]
    public partial bool IsConnected { get; internal set; }

    [State(IsDiscrete = true)]
    public partial ServiceStatus Status { get; internal set; }

    [State]
    public partial string? StatusMessage { get; internal set; }

    [State]
    public partial DateTimeOffset? LastUpdated { get; internal set; }

    // Single-instance sensors (nullable)
    [State(Position = 10)]
    public partial EcowittOutdoorSensor? OutdoorSensor { get; internal set; }

    [State(Position = 11)]
    public partial EcowittIndoorSensor? IndoorSensor { get; internal set; }

    [State(Position = 12)]
    public partial EcowittRainGauge? RainGauge { get; internal set; }

    [State(Position = 13)]
    public partial EcowittRainGauge? PiezoRainGauge { get; internal set; }

    [State(Position = 14)]
    public partial EcowittLightningSensor? LightningSensor { get; internal set; }

    // Array sensors (indexed by channel)
    [State(Position = 20)]
    public partial EcowittChannelSensor[] ChannelSensors { get; internal set; }

    [State(Position = 21)]
    public partial EcowittSoilMoistureSensor[] SoilMoistureSensors { get; internal set; }

    [State(Position = 22)]
    public partial EcowittLeafWetnessSensor[] LeafWetnessSensors { get; internal set; }

    [State(Position = 23)]
    public partial EcowittTemperatureSensor[] TemperatureSensors { get; internal set; }

    [State(Position = 24)]
    public partial EcowittPm25Sensor[] Pm25Sensors { get; internal set; }

    [State(Position = 25)]
    public partial EcowittCo2Sensor[] Co2Sensors { get; internal set; }

    [State(Position = 26)]
    public partial EcowittLeakSensor[] LeakSensors { get; internal set; }

    // Derived properties
    [Derived]
    [State]
    public string? SoftwareVersion => _versionInfo?.Version;

    [Derived]
    [State]
    public string? AvailableSoftwareUpdate => null;

    [Derived]
    [State]
    public string? MacAddress => _networkInfo?.MacAddress;

    [Derived]
    [State]
    public string? IpAddress => _networkInfo?.IpAddress;

    public string? SubnetMask => _networkInfo?.SubnetMask;
    public string? Gateway => _networkInfo?.Gateway;
    public bool? IsWireless => _networkInfo?.IsWireless;
    public int? SignalStrength => null;

    // IDeviceInfo
    [Derived]
    [State]
    public string? Manufacturer => "Ecowitt";

    [Derived]
    [State]
    public string? Model => _deviceInfo?.Model;

    [Derived]
    [State]
    public string? ProductCode => _deviceInfo?.StationType;

    [Derived]
    [State]
    public string? SerialNumber => _networkInfo?.MacAddress;

    [Derived]
    [State]
    public string? HardwareRevision => null;

    [Derived]
    public string? Title => !string.IsNullOrEmpty(Name) ? Name :
        _deviceInfo?.Model ?? HostAddress;

    [Derived]
    public string IconName => IsConnected ? "Cloud" : "CloudOff";

    [Derived]
    public string IconColor =>
        IsConnected ? "Success" :
        Status == ServiceStatus.Error ? "Error" : "Warning";

    public EcowittGateway(IHttpClientFactory httpClientFactory, ILogger<EcowittGateway> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;

        Name = string.Empty;
        HostAddress = null;
        PollingInterval = TimeSpan.FromSeconds(30);
        RetryInterval = TimeSpan.FromSeconds(60);
        HiddenSensors = [];
        RainCumulativeOffset = 0;
        RainLastMonthlyValue = 0;
        PiezoRainCumulativeOffset = 0;
        PiezoRainLastMonthlyValue = 0;

        IsConnected = false;
        Status = ServiceStatus.Stopped;
        StatusMessage = null;
        LastUpdated = null;
        OutdoorSensor = null;
        IndoorSensor = null;
        RainGauge = null;
        PiezoRainGauge = null;
        LightningSensor = null;
        ChannelSensors = [];
        SoilMoistureSensors = [];
        LeafWetnessSensors = [];
        TemperatureSensors = [];
        Pm25Sensors = [];
        Co2Sensors = [];
        LeakSensors = [];
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

                using var httpClient = _httpClientFactory.CreateClient();
                httpClient.Timeout = TimeSpan.FromSeconds(10);

                var client = new EcowittClient(httpClient, HostAddress);

                // Fetch device info once on connect
                var versionTask = client.GetVersionAsync(stoppingToken);
                var networkTask = client.GetNetworkInfoAsync(stoppingToken);
                var deviceInfoTask = client.GetDeviceInfoAsync(stoppingToken);
                await Task.WhenAll(versionTask, networkTask, deviceInfoTask);

                _versionInfo = versionTask.Result;
                _networkInfo = networkTask.Result;
                _deviceInfo = deviceInfoTask.Result;
                _sensorsInfo = await client.GetSensorsInfoAsync(stoppingToken);

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
                _logger.LogError(exception, "Ecowitt gateway {HostAddress} connection failed", HostAddress);
                IsConnected = false;
                Status = ServiceStatus.Error;
                StatusMessage = exception.Message;
                ResetState();

                try
                {
                    await Task.Delay(RetryInterval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        Status = ServiceStatus.Stopped;
        StatusMessage = null;
    }

    private async Task RunPollingLoopAsync(EcowittClient client, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var liveData = await client.GetLiveDataAsync(stoppingToken);
                var configChanged = UpdateFromLiveData(liveData);
                LastUpdated = DateTimeOffset.UtcNow;

                if (configChanged)
                {
                    var configWriter = this.TryGetFirstParent<IConfigurationWriter>();
                    if (configWriter != null)
                        await configWriter.WriteConfigurationAsync(this, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Ecowitt gateway {HostAddress} poll failed", HostAddress);
                IsConnected = false;
                Status = ServiceStatus.Error;
                StatusMessage = exception.Message;

                try
                {
                    await Task.Delay(RetryInterval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    // stopping
                }
                return;
            }

            try
            {
                var signaled = await _configChangedSignal.WaitAsync(PollingInterval, stoppingToken);
                if (signaled)
                {
                    _versionInfo = null;
                    _networkInfo = null;
                    _deviceInfo = null;
                    return;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    internal bool UpdateFromLiveData(EcowittLiveData data)
    {
        var now = DateTimeOffset.UtcNow;

        // Single-instance sensors: create on first discovery, mutate in-place
        if (data.Outdoor != null)
        {
            OutdoorSensor ??= new EcowittOutdoorSensor();
            OutdoorSensor.Temperature = data.Outdoor.Temperature;
            OutdoorSensor.Humidity = data.Outdoor.Humidity;
            OutdoorSensor.DewPoint = data.Outdoor.DewPoint;
            OutdoorSensor.FeelsLikeTemperature = data.Outdoor.FeelsLikeTemperature;
            OutdoorSensor.WindSpeed = data.Outdoor.WindSpeed;
            OutdoorSensor.WindGust = data.Outdoor.WindGust;
            OutdoorSensor.MaxDailyGust = data.Outdoor.MaxDailyGust;
            OutdoorSensor.WindDirection = data.Outdoor.WindDirection;
            OutdoorSensor.Illuminance = data.Outdoor.Illuminance;
            OutdoorSensor.UvIndex = data.Outdoor.UvIndex;
            OutdoorSensor.SolarRadiation = data.Outdoor.SolarRadiation;
            OutdoorSensor.VaporPressureDeficit = data.Outdoor.VaporPressureDeficit;
            OutdoorSensor.LastUpdated = now;
        }

        if (data.Indoor != null)
        {
            IndoorSensor ??= new EcowittIndoorSensor();
            IndoorSensor.Temperature = data.Indoor.Temperature;
            IndoorSensor.Humidity = data.Indoor.Humidity;
            IndoorSensor.AbsolutePressure = data.Indoor.AbsolutePressure;
            IndoorSensor.RelativePressure = data.Indoor.RelativePressure;
            IndoorSensor.LastUpdated = now;
        }

        var configChanged = false;

        if (data.Rain != null && !IsSensorHidden("rain"))
        {
            RainGauge ??= new EcowittRainGauge("Rain Gauge");
            var longestBucket = data.Rain.YearlyRain ?? data.Rain.MonthlyRain;
            var previousOffset = RainCumulativeOffset;
            var cumulativeOffset = RainCumulativeOffset;
            var lastBucketValue = RainLastMonthlyValue;
            var cumulativeTotal = AccumulateRain(longestBucket, ref cumulativeOffset, ref lastBucketValue);
            RainCumulativeOffset = cumulativeOffset;
            RainLastMonthlyValue = lastBucketValue;
            configChanged |= cumulativeOffset != previousOffset;
            UpdateRainGauge(RainGauge, data.Rain, cumulativeTotal, now);
        }
        else if (IsSensorHidden("rain"))
        {
            RainGauge = null;
        }

        if (data.PiezoRain != null && !IsSensorHidden("piezo"))
        {
            PiezoRainGauge ??= new EcowittRainGauge("Piezo Rain");
            var longestBucket = data.PiezoRain.YearlyRain ?? data.PiezoRain.MonthlyRain;
            var previousPiezoOffset = PiezoRainCumulativeOffset;
            var piezoOffset = PiezoRainCumulativeOffset;
            var piezoLastValue = PiezoRainLastMonthlyValue;
            var cumulativeTotal = AccumulateRain(longestBucket, ref piezoOffset, ref piezoLastValue);
            PiezoRainCumulativeOffset = piezoOffset;
            PiezoRainLastMonthlyValue = piezoLastValue;
            configChanged |= piezoOffset != previousPiezoOffset;
            UpdateRainGauge(PiezoRainGauge, data.PiezoRain, cumulativeTotal, now);
        }
        else if (IsSensorHidden("piezo"))
        {
            PiezoRainGauge = null;
        }

        if (data.Lightning != null && !IsSensorHidden("lightning"))
        {
            LightningSensor ??= new EcowittLightningSensor();
            LightningSensor.Distance = data.Lightning.Distance;
            LightningSensor.StrikeCount = data.Lightning.StrikeCount;
            LightningSensor.LastStrikeTime = data.Lightning.LastStrikeTime;
            LightningSensor.BatteryLevel = EcowittValueParser.NormalizeBatteryLevel(data.Lightning.Battery, isBinaryBattery: false);
            LightningSensor.LastUpdated = now;
        }
        else if (IsSensorHidden("lightning"))
        {
            LightningSensor = null;
        }

        // Array sensors: filter hidden, create once, update in-place
        UpdateChannelSensors(data.Channels, now);
        UpdateSoilMoistureSensors(data.SoilMoisture, now);
        UpdateLeafWetnessSensors(data.LeafWetness, now);
        UpdateTemperatureSensors(data.Temperatures, now);
        UpdatePm25Sensors(data.Pm25, now);
        UpdateCo2Sensors(data.Co2, now);
        UpdateLeakSensors(data.Leaks, now);

        if (_sensorsInfo != null)
            UpdateSensorInfo(_sensorsInfo);

        IsConnected = true;
        return configChanged;
    }

    internal void UpdateSensorInfo(EcowittSensorInfo[] sensorsInfo)
    {
        // Prefer WS90 (48) / WH85 (49) over WH68 (1) / WH80 (2) / WH69 (0) for outdoor sensor
        var outdoorSensorInfo = sensorsInfo
            .Where(s => s.TypeCode is 0 or 1 or 2 or 48 or 49)
            .OrderByDescending(s => s.TypeCode is 48 or 49 ? 1 : 0)
            .FirstOrDefault();

        if (outdoorSensorInfo != null)
        {
            if (OutdoorSensor != null)
            {
                OutdoorSensor.SensorId = outdoorSensorInfo.SensorId;
                OutdoorSensor.SignalStrength = outdoorSensorInfo.Rssi;
                OutdoorSensor.BatteryLevel = EcowittValueParser.NormalizeBatteryLevel(
                    outdoorSensorInfo.Battery, isBinaryBattery: false);
            }

            // WS90 (48) and WH85 (49) also provide piezo rain data
            if (outdoorSensorInfo.TypeCode is 48 or 49 && PiezoRainGauge != null)
            {
                PiezoRainGauge.SensorId = outdoorSensorInfo.SensorId;
                PiezoRainGauge.SignalStrength = outdoorSensorInfo.Rssi;
            }
        }

        foreach (var sensorInfo in sensorsInfo)
        {
            switch (sensorInfo.TypeCode)
            {
                // Outdoor sensors handled above
                case 0 or 1 or 2 or 48 or 49:
                    break;

                // Rain: WH40 (3)
                case 3 when RainGauge != null:
                    RainGauge.SensorId = sensorInfo.SensorId;
                    RainGauge.SignalStrength = sensorInfo.Rssi;
                    break;

                // Indoor: WH25 (4)
                case 4 when IndoorSensor != null:
                    IndoorSensor.SensorId = sensorInfo.SensorId;
                    IndoorSensor.SignalStrength = sensorInfo.Rssi;
                    break;

                // Channel 1-8: types 6-13
                case >= 6 and <= 13:
                    var channelNumber = sensorInfo.TypeCode - 5;
                    var channelSensor = ChannelSensors.FirstOrDefault(s => s.Channel == channelNumber);
                    if (channelSensor != null)
                    {
                        channelSensor.SensorId = sensorInfo.SensorId;
                        channelSensor.SignalStrength = sensorInfo.Rssi;
                    }
                    break;

                // Soil moisture 1-8: types 14-21, 9-16: types 58-65
                case >= 14 and <= 21:
                    var soilChannel1 = sensorInfo.TypeCode - 13;
                    var soilSensor1 = SoilMoistureSensors.FirstOrDefault(s => s.Channel == soilChannel1);
                    if (soilSensor1 != null)
                    {
                        soilSensor1.SensorId = sensorInfo.SensorId;
                        soilSensor1.SignalStrength = sensorInfo.Rssi;
                    }
                    break;
                case >= 58 and <= 65:
                    var soilChannel2 = sensorInfo.TypeCode - 49;
                    var soilSensor2 = SoilMoistureSensors.FirstOrDefault(s => s.Channel == soilChannel2);
                    if (soilSensor2 != null)
                    {
                        soilSensor2.SensorId = sensorInfo.SensorId;
                        soilSensor2.SignalStrength = sensorInfo.Rssi;
                    }
                    break;

                // PM2.5 1-4: types 22-25
                case >= 22 and <= 25:
                    var pm25Channel = sensorInfo.TypeCode - 21;
                    var pm25Sensor = Pm25Sensors.FirstOrDefault(s => s.Channel == pm25Channel);
                    if (pm25Sensor != null)
                    {
                        pm25Sensor.SensorId = sensorInfo.SensorId;
                        pm25Sensor.SignalStrength = sensorInfo.Rssi;
                    }
                    break;

                // Lightning: type 26
                case 26 when LightningSensor != null:
                    LightningSensor.SensorId = sensorInfo.SensorId;
                    LightningSensor.SignalStrength = sensorInfo.Rssi;
                    break;

                // Leak 1-4: types 27-30
                case >= 27 and <= 30:
                    var leakChannel = sensorInfo.TypeCode - 26;
                    var leakSensor = LeakSensors.FirstOrDefault(s => s.Channel == leakChannel);
                    if (leakSensor != null)
                    {
                        leakSensor.SensorId = sensorInfo.SensorId;
                        leakSensor.SignalStrength = sensorInfo.Rssi;
                    }
                    break;

                // Temp 1-8: types 31-38
                case >= 31 and <= 38:
                    var tempChannel = sensorInfo.TypeCode - 30;
                    var tempSensor = TemperatureSensors.FirstOrDefault(s => s.Channel == tempChannel);
                    if (tempSensor != null)
                    {
                        tempSensor.SensorId = sensorInfo.SensorId;
                        tempSensor.SignalStrength = sensorInfo.Rssi;
                    }
                    break;

                // Leaf 1-8: types 40-47
                case >= 40 and <= 47:
                    var leafChannel = sensorInfo.TypeCode - 39;
                    var leafSensor = LeafWetnessSensors.FirstOrDefault(s => s.Channel == leafChannel);
                    if (leafSensor != null)
                    {
                        leafSensor.SensorId = sensorInfo.SensorId;
                        leafSensor.SignalStrength = sensorInfo.Rssi;
                    }
                    break;
            }
        }
    }

    /// <summary>
    /// Builds a monotonically increasing total from a periodic bucket that resets
    /// (e.g., yearly rain resets to 0 on Jan 1). Detects resets when the current
    /// value drops below the previous value and accumulates the offset.
    /// </summary>
    private static decimal? AccumulateRain(
        decimal? currentBucketValue,
        ref decimal cumulativeOffset,
        ref decimal lastBucketValue)
    {
        if (currentBucketValue == null)
            return null;

        // Detect bucket reset (e.g. monthly rain resets to 0 at start of new month)
        if (currentBucketValue < lastBucketValue)
            cumulativeOffset += lastBucketValue;

        lastBucketValue = currentBucketValue.Value;
        return cumulativeOffset + currentBucketValue.Value;
    }

    private static void UpdateRainGauge(
        EcowittRainGauge gauge,
        EcowittRainData data,
        decimal? cumulativeTotal,
        DateTimeOffset now)
    {
        gauge.RainEvent = data.RainEvent;
        gauge.RainRate = data.RainRate;
        gauge.HourlyRain = data.HourlyRain;
        gauge.DailyRain = data.DailyRain;
        gauge.WeeklyRain = data.WeeklyRain;
        gauge.MonthlyRain = data.MonthlyRain;
        gauge.YearlyRain = data.YearlyRain;
        gauge.TotalRain = cumulativeTotal;
        gauge.BatteryLevel = EcowittValueParser.NormalizeBatteryLevel(data.Battery, isBinaryBattery: false);
        gauge.LastUpdated = now;
    }

    // Battery in the local HTTP API: WH31 channels (ch_aisle) use binary (0=OK, 1=Low).
    // All other sensor types use a 0-5 level scale. See Ecowitt.md for the full classification.
    private void UpdateChannelSensors(EcowittChannelData[] channels, DateTimeOffset now)
    {
        var visible = channels.Where(c => !IsSensorHidden($"ch:{c.Channel}")).ToArray();

        if (ChannelSensors.Length != visible.Length ||
            !ChannelSensors.Select(s => s.Channel).SequenceEqual(visible.Select(c => c.Channel)))
        {
            ChannelSensors = visible.Select(c => new EcowittChannelSensor(c.Channel)).ToArray();
        }

        for (var i = 0; i < visible.Length; i++)
        {
            ChannelSensors[i].Temperature = visible[i].Temperature;
            ChannelSensors[i].Humidity = visible[i].Humidity;
            ChannelSensors[i].BatteryLevel = EcowittValueParser.NormalizeBatteryLevel(visible[i].Battery, isBinaryBattery: true);
            ChannelSensors[i].LastUpdated = now;
        }
    }

    private void UpdateSoilMoistureSensors(EcowittSoilData[] soilData, DateTimeOffset now)
    {
        var visible = soilData.Where(s => !IsSensorHidden($"soil:{s.Channel}")).ToArray();

        if (SoilMoistureSensors.Length != visible.Length ||
            !SoilMoistureSensors.Select(s => s.Channel).SequenceEqual(visible.Select(s => s.Channel)))
        {
            SoilMoistureSensors = visible.Select(s => new EcowittSoilMoistureSensor(s.Channel)).ToArray();
        }

        for (var i = 0; i < visible.Length; i++)
        {
            SoilMoistureSensors[i].SoilMoisture = visible[i].Moisture;
            SoilMoistureSensors[i].BatteryLevel = EcowittValueParser.NormalizeBatteryLevel(visible[i].Battery, isBinaryBattery: false);
            SoilMoistureSensors[i].LastUpdated = now;
        }
    }

    private void UpdateLeafWetnessSensors(EcowittLeafData[] leafData, DateTimeOffset now)
    {
        var visible = leafData.Where(l => !IsSensorHidden($"leaf:{l.Channel}")).ToArray();

        if (LeafWetnessSensors.Length != visible.Length ||
            !LeafWetnessSensors.Select(s => s.Channel).SequenceEqual(visible.Select(l => l.Channel)))
        {
            LeafWetnessSensors = visible.Select(l => new EcowittLeafWetnessSensor(l.Channel)).ToArray();
        }

        for (var i = 0; i < visible.Length; i++)
        {
            LeafWetnessSensors[i].LeafWetness = visible[i].Wetness;
            LeafWetnessSensors[i].BatteryLevel = EcowittValueParser.NormalizeBatteryLevel(visible[i].Battery, isBinaryBattery: false);
            LeafWetnessSensors[i].LastUpdated = now;
        }
    }

    private void UpdateTemperatureSensors(EcowittTemperatureData[] tempData, DateTimeOffset now)
    {
        var visible = tempData.Where(t => !IsSensorHidden($"temp:{t.Channel}")).ToArray();

        if (TemperatureSensors.Length != visible.Length ||
            !TemperatureSensors.Select(s => s.Channel).SequenceEqual(visible.Select(t => t.Channel)))
        {
            TemperatureSensors = visible.Select(t => new EcowittTemperatureSensor(t.Channel)).ToArray();
        }

        for (var i = 0; i < visible.Length; i++)
        {
            TemperatureSensors[i].Temperature = visible[i].Temperature;
            TemperatureSensors[i].BatteryLevel = EcowittValueParser.NormalizeBatteryLevel(visible[i].Battery, isBinaryBattery: false);
            TemperatureSensors[i].LastUpdated = now;
        }
    }

    private void UpdatePm25Sensors(EcowittPm25Data[] pm25Data, DateTimeOffset now)
    {
        var visible = pm25Data.Where(p => !IsSensorHidden($"pm25:{p.Channel}")).ToArray();

        if (Pm25Sensors.Length != visible.Length ||
            !Pm25Sensors.Select(s => s.Channel).SequenceEqual(visible.Select(p => p.Channel)))
        {
            Pm25Sensors = visible.Select(p => new EcowittPm25Sensor(p.Channel)).ToArray();
        }

        for (var i = 0; i < visible.Length; i++)
        {
            Pm25Sensors[i].Pm25 = visible[i].Pm25;
            Pm25Sensors[i].Pm25Avg24h = visible[i].Pm25Avg24h;
            Pm25Sensors[i].BatteryLevel = EcowittValueParser.NormalizeBatteryLevel(visible[i].Battery, isBinaryBattery: false);
            Pm25Sensors[i].LastUpdated = now;
        }
    }

    private void UpdateCo2Sensors(EcowittCo2Data[] co2Data, DateTimeOffset now)
    {
        var visible = co2Data.Where(c => !IsSensorHidden($"co2:{c.Channel}")).ToArray();

        if (Co2Sensors.Length != visible.Length ||
            !Co2Sensors.Select(s => s.Channel).SequenceEqual(visible.Select(c => c.Channel)))
        {
            Co2Sensors = visible.Select(c => new EcowittCo2Sensor(c.Channel)).ToArray();
        }

        for (var i = 0; i < visible.Length; i++)
        {
            Co2Sensors[i].Co2 = visible[i].Co2;
            Co2Sensors[i].Co2Avg24h = visible[i].Co2Avg24h;
            Co2Sensors[i].Temperature = visible[i].Temperature;
            Co2Sensors[i].Humidity = visible[i].Humidity;
            Co2Sensors[i].BatteryLevel = EcowittValueParser.NormalizeBatteryLevel(visible[i].Battery, isBinaryBattery: false);
            Co2Sensors[i].LastUpdated = now;
        }
    }

    private void UpdateLeakSensors(EcowittLeakData[] leakData, DateTimeOffset now)
    {
        var visible = leakData.Where(l => !IsSensorHidden($"leak:{l.Channel}")).ToArray();

        if (LeakSensors.Length != visible.Length ||
            !LeakSensors.Select(s => s.Channel).SequenceEqual(visible.Select(l => l.Channel)))
        {
            LeakSensors = visible.Select(l => new EcowittLeakSensor(l.Channel)).ToArray();
        }

        for (var i = 0; i < visible.Length; i++)
        {
            LeakSensors[i].IsLeaking = visible[i].IsLeaking;
            LeakSensors[i].BatteryLevel = EcowittValueParser.NormalizeBatteryLevel(visible[i].Battery, isBinaryBattery: false);
            LeakSensors[i].LastUpdated = now;
        }
    }

    private bool IsSensorHidden(string sensorKey) =>
        HiddenSensors.Contains(sensorKey, StringComparer.OrdinalIgnoreCase);

    private void ResetState()
    {
        OutdoorSensor = null;
        IndoorSensor = null;
        RainGauge = null;
        PiezoRainGauge = null;
        LightningSensor = null;
        ChannelSensors = [];
        SoilMoistureSensors = [];
        LeafWetnessSensors = [];
        TemperatureSensors = [];
        Pm25Sensors = [];
        Co2Sensors = [];
        LeakSensors = [];
    }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default)
    {
        try { _configChangedSignal.Release(); }
        catch (SemaphoreFullException) { }
        return Task.CompletedTask;
    }
}
