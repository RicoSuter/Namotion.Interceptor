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

[Category("Climate")]
[Description("Ecowitt weather station gateway with dynamic sensor discovery")]
[DisplayName("Ecowitt gateway")]
[SubjectIcon("Cloud")]
[InterceptorSubject]
public partial class EcowittGateway : BackgroundService,
    IConfigurable,
    IMonitoredService,
    ITitleProvider,
    IIconProvider,
    ILastUpdatedProvider,
    IConnectionState,
    INetworkAdapter,
    INetworkHost,
    ISoftwareState,
    IDeviceInfo,
    IHubDevice
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<EcowittGateway> _logger;
    private readonly SemaphoreSlim _configChangedSignal = new(0, 1);

    // HiddenSensors as of the running Update, read once per update.
    private string[] _hiddenSensors = [];

    internal TimeProvider Clock { get; init; } = TimeProvider.System;

    // Whether the last read of the sensor list failed, so that a failing list is logged once and not on every poll.
    private bool _isSensorListFailing;

    // The rain totals change with every poll while it rains, so of the writes in a row that wrote nothing
    // only the first is logged.
    private bool _isConfigurationUnsaved;

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

    // Tracked, so that the derived properties that read it change when it is fetched or reset.
    internal partial EcowittVersionInfo? VersionInfo { get; set; }
    internal partial EcowittNetworkInfo? NetworkInfo { get; set; }
    internal partial EcowittDeviceInfo? DeviceInfo { get; set; }

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

    /// <summary>
    /// Gets the sensors paired with the gateway, keyed by sensor ID, with their device health.
    /// </summary>
    [State(Position = 30)]
    public partial Dictionary<string, EcowittSensor> Sensors { get; internal set; }

    // Derived properties
    [Derived]
    [State]
    public string? SoftwareVersion => VersionInfo?.Version;

    [Derived]
    [State]
    public string? AvailableSoftwareUpdate => null;

    [Derived]
    [State]
    public string? MacAddress => NetworkInfo?.MacAddress;

    [Derived]
    [State]
    public string? IpAddress => NetworkInfo?.IpAddress;

    public string? SubnetMask => NetworkInfo?.SubnetMask;
    public string? Gateway => NetworkInfo?.Gateway;
    public bool? IsWireless => NetworkInfo?.IsWireless;
    public int? SignalStrength => null;

    // IDeviceInfo
    [Derived]
    [State]
    public string? Manufacturer => "Ecowitt";

    [Derived]
    [State]
    public string? Model => DeviceInfo?.Model;

    [Derived]
    [State]
    public string? ProductCode => DeviceInfo?.StationType;

    [Derived]
    [State]
    public string? SerialNumber => NetworkInfo?.MacAddress;

    [Derived]
    [State]
    public string? HardwareRevision => null;

    [Derived]
    public string? Title => !string.IsNullOrEmpty(Name) ? Name :
        DeviceInfo?.Model ?? HostAddress;

    [Derived]
    public string IconName => IsConnected ? "Cloud" : "CloudOff";

    [Derived]
    public string? IconColor => IsConnected ? "Success" : null;

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
        Sensors = new Dictionary<string, EcowittSensor>(StringComparer.Ordinal);
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

                VersionInfo = await versionTask;
                NetworkInfo = await networkTask;
                DeviceInfo = await deviceInfoTask;

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
                // The sensor list is read on every poll, so that LastSeen follows the reception of each sensor and
                // sensors paired or removed in the gateway appear or disappear.
                var liveDataTask = client.GetLiveDataAsync(stoppingToken);
                var sensorsInfoTask = TryGetSensorsInfoAsync(client, stoppingToken);
                await Task.WhenAll(liveDataTask, sensorsInfoTask);

                var configChanged = Update(await liveDataTask, await sensorsInfoTask);
                LastUpdated = DateTimeOffset.UtcNow;

                if (configChanged)
                {
                    await SaveConfigurationAsync(stoppingToken);
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
                MarkSensorsOffline();

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
                    VersionInfo = null;
                    NetworkInfo = null;
                    DeviceInfo = null;
                    return;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    /// <returns>The sensor list, or null when it could not be read, so that the poll keeps the current sensors.</returns>
    private async Task<EcowittSensorInfo[]?> TryGetSensorsInfoAsync(EcowittClient client, CancellationToken cancellationToken)
    {
        try
        {
            var sensorsInfo = await client.GetSensorsInfoAsync(cancellationToken);
            _isSensorListFailing = false;
            return sensorsInfo;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            if (!_isSensorListFailing)
            {
                _logger.LogWarning(exception,
                    "Ecowitt gateway {HostAddress} sensor list could not be read, the sensors keep their last state", HostAddress);
            }

            _isSensorListFailing = true;
            return null;
        }
    }

    internal void MarkSensorsOffline()
    {
        foreach (var sensor in Sensors.Values)
            sensor.MarkOffline();
    }

    internal async Task SaveConfigurationAsync(CancellationToken cancellationToken)
    {
        var configurationWriter = this.TryGetFirstParent<IConfigurationWriter>();
        if (configurationWriter == null)
            return;

        var isWritten = await configurationWriter.WriteConfigurationAsync(this, cancellationToken);
        if (!isWritten && !_isConfigurationUnsaved)
        {
            _logger.LogWarning(
                "Ecowitt gateway {HostAddress} rain totals were not saved and will be lost on restart", HostAddress);
        }

        _isConfigurationUnsaved = !isWritten;
    }

    /// <summary>
    /// Applies a poll of the gateway: the sensor list when given, then the live data, then the sources of the readings.
    /// </summary>
    /// <param name="data">The live data.</param>
    /// <param name="sensorsInfo">The sensor list, or null to keep the current sensors.</param>
    /// <returns>Whether the configuration changed and needs to be saved.</returns>
    internal bool Update(EcowittLiveData data, EcowittSensorInfo[]? sensorsInfo = null)
    {
        var now = Clock.GetUtcNow();
        _hiddenSensors = HiddenSensors;

        // A sensor takes the context of the parent that attaches it first as fallback, and releases the fallback
        // with the parent that detaches it last. So Sensors adds a sensor before any Sources refers to it and drops
        // it only after every Sources has. This holds while the gateway is attached during the update; attaching an
        // already populated gateway attaches its properties in hash order (#803 and #501 remove this dependency).
        var sensors = sensorsInfo != null ? UpdateSensors(sensorsInfo, now) : Sensors;

        // Single-instance sensors: create on first discovery, mutate in-place
        if (data.Outdoor != null && !IsSensorHidden("outdoor"))
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
        else if (IsSensorHidden("outdoor"))
        {
            OutdoorSensor = null;
        }

        if (data.Indoor != null && !IsSensorHidden("indoor"))
        {
            IndoorSensor ??= new EcowittIndoorSensor();
            IndoorSensor.Temperature = data.Indoor.Temperature;
            IndoorSensor.Humidity = data.Indoor.Humidity;
            IndoorSensor.AbsolutePressure = data.Indoor.AbsolutePressure;
            IndoorSensor.RelativePressure = data.Indoor.RelativePressure;
            IndoorSensor.LastUpdated = now;
        }
        else if (IsSensorHidden("indoor"))
        {
            IndoorSensor = null;
        }

        var configChanged = false;

        if (data.Rain != null && !IsSensorHidden("rain"))
        {
            RainGauge ??= new EcowittRainGauge("Rain Gauge");
            var longestBucket = GetLongestRainBucket(data.Rain);
            var previousLastBucketValue = RainLastMonthlyValue;
            var cumulativeOffset = RainCumulativeOffset;
            var lastBucketValue = RainLastMonthlyValue;
            var cumulativeTotal = AccumulateRain(longestBucket, ref cumulativeOffset, ref lastBucketValue);
            RainCumulativeOffset = cumulativeOffset;
            RainLastMonthlyValue = lastBucketValue;
            configChanged |= lastBucketValue != previousLastBucketValue;
            UpdateRainGauge(RainGauge, data.Rain, cumulativeTotal, now);
        }
        else if (IsSensorHidden("rain"))
        {
            RainGauge = null;
        }

        if (data.PiezoRain != null && !IsSensorHidden("piezo"))
        {
            PiezoRainGauge ??= new EcowittRainGauge("Piezo Rain");
            var longestBucket = GetLongestRainBucket(data.PiezoRain);
            var previousPiezoLastValue = PiezoRainLastMonthlyValue;
            var piezoOffset = PiezoRainCumulativeOffset;
            var piezoLastValue = PiezoRainLastMonthlyValue;
            var cumulativeTotal = AccumulateRain(longestBucket, ref piezoOffset, ref piezoLastValue);
            PiezoRainCumulativeOffset = piezoOffset;
            PiezoRainLastMonthlyValue = piezoLastValue;
            configChanged |= piezoLastValue != previousPiezoLastValue;
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

        UpdateSources(sensors);
        if (!ReferenceEquals(Sensors, sensors))
            Sensors = sensors;

        IsConnected = true;
        return configChanged;
    }

    /// <returns>The sensors after the update, which Update assigns to Sensors after the sources.</returns>
    private Dictionary<string, EcowittSensor> UpdateSensors(EcowittSensorInfo[] sensorsInfo, DateTimeOffset now)
    {
        var current = Sensors;
        var isChanged = false;
        foreach (var sensorInfo in sensorsInfo)
        {
            if (IsSensorVisible(sensorInfo) && !current.ContainsKey(sensorInfo.SensorId!))
            {
                isChanged = true;
                break;
            }
        }

        if (!isChanged)
        {
            foreach (var sensorId in current.Keys)
            {
                if (!ContainsVisibleSensor(sensorsInfo, sensorId))
                {
                    isChanged = true;
                    break;
                }
            }
        }

        var next = isChanged
            ? new Dictionary<string, EcowittSensor>(sensorsInfo.Length, StringComparer.Ordinal)
            : current;

        foreach (var sensorInfo in sensorsInfo)
        {
            if (!IsSensorVisible(sensorInfo))
                continue;

            var sensorId = sensorInfo.SensorId!;
            if (!current.TryGetValue(sensorId, out var sensor) && !next.TryGetValue(sensorId, out sensor))
                sensor = new EcowittSensor(sensorId);

            sensor.Update(sensorInfo, now);
            if (isChanged)
                next[sensorId] = sensor;
        }

        if (!isChanged)
            return current;

        var hasAddedSensors = false;
        var hasRemovedSensors = false;
        foreach (var sensorId in next.Keys)
            hasAddedSensors |= !current.ContainsKey(sensorId);
        foreach (var sensorId in current.Keys)
            hasRemovedSensors |= !next.ContainsKey(sensorId);

        if (!hasRemovedSensors)
        {
            Sensors = next;
        }
        else if (hasAddedSensors)
        {
            // Update assigns next once the removed sensors have left their Sources; until then the added ones join
            // the removed ones, so that Sensors still attaches them before any Sources.
            var union = new Dictionary<string, EcowittSensor>(current, StringComparer.Ordinal);
            foreach (var (sensorId, sensor) in next)
                union[sensorId] = sensor;
            Sensors = union;
        }

        return next;
    }

    private bool ContainsVisibleSensor(EcowittSensorInfo[] sensorsInfo, string sensorId)
    {
        foreach (var sensorInfo in sensorsInfo)
        {
            if (sensorInfo.SensorId == sensorId && IsSensorVisible(sensorInfo))
                return true;
        }

        return false;
    }

    private bool IsSensorVisible(EcowittSensorInfo sensorInfo) =>
        sensorInfo.SensorId != null && !IsSensorHidden(sensorInfo.TypeCode);

    /// <summary>
    /// Gets whether every reading a sensor of the type feeds is hidden, so that a hidden neighbor's sensor is left out
    /// of Sensors too. A type that feeds no known reading is never hidden.
    /// </summary>
    private bool IsSensorHidden(int typeCode)
    {
        if (_hiddenSensors.Length == 0)
            return false;

        if (EcowittSensorTypes.TryGetChannelSlot(typeCode, out var reading, out var channel))
            return IsSensorHidden(GetHiddenSensorKey(reading, channel));

        var feedsAnyReading = false;
        foreach (var mergedReading in MergedReadings)
        {
            if (!EcowittSensorTypes.Feeds(typeCode, mergedReading))
                continue;

            if (!IsSensorHidden(GetHiddenSensorKey(mergedReading, 0)))
                return false;

            feedsAnyReading = true;
        }

        return feedsAnyReading;
    }

    private static readonly EcowittReading[] MergedReadings =
    [
        EcowittReading.Outdoor,
        EcowittReading.Indoor,
        EcowittReading.Rain,
        EcowittReading.PiezoRain,
        EcowittReading.Lightning
    ];

    private static string GetHiddenSensorKey(EcowittReading reading, int channel) => reading switch
    {
        EcowittReading.Outdoor => "outdoor",
        EcowittReading.Indoor => "indoor",
        EcowittReading.Rain => "rain",
        EcowittReading.PiezoRain => "piezo",
        EcowittReading.Lightning => "lightning",
        EcowittReading.Channel => $"ch:{channel}",
        EcowittReading.SoilMoisture => $"soil:{channel}",
        EcowittReading.LeafWetness => $"leaf:{channel}",
        EcowittReading.Temperature => $"temp:{channel}",
        EcowittReading.Pm25 => $"pm25:{channel}",
        EcowittReading.Leak => $"leak:{channel}",
        _ => throw new ArgumentOutOfRangeException(nameof(reading), reading, null)
    };

    private void UpdateSources(Dictionary<string, EcowittSensor> sensors)
    {
        if (OutdoorSensor != null)
            OutdoorSensor.Sources = SelectSources(sensors, OutdoorSensor.Sources, EcowittReading.Outdoor, 0);
        if (IndoorSensor != null)
            IndoorSensor.Sources = SelectSources(sensors, IndoorSensor.Sources, EcowittReading.Indoor, 0);
        if (RainGauge != null)
            RainGauge.Sources = SelectSources(sensors, RainGauge.Sources, EcowittReading.Rain, 0);
        if (PiezoRainGauge != null)
            PiezoRainGauge.Sources = SelectSources(sensors, PiezoRainGauge.Sources, EcowittReading.PiezoRain, 0);
        if (LightningSensor != null)
            LightningSensor.Sources = SelectSources(sensors, LightningSensor.Sources, EcowittReading.Lightning, 0);

        foreach (var sensor in ChannelSensors)
            sensor.Sources = SelectSources(sensors, sensor.Sources, EcowittReading.Channel, sensor.Channel);
        foreach (var sensor in SoilMoistureSensors)
            sensor.Sources = SelectSources(sensors, sensor.Sources, EcowittReading.SoilMoisture, sensor.Channel);
        foreach (var sensor in LeafWetnessSensors)
            sensor.Sources = SelectSources(sensors, sensor.Sources, EcowittReading.LeafWetness, sensor.Channel);
        foreach (var sensor in TemperatureSensors)
            sensor.Sources = SelectSources(sensors, sensor.Sources, EcowittReading.Temperature, sensor.Channel);
        foreach (var sensor in Pm25Sensors)
            sensor.Sources = SelectSources(sensors, sensor.Sources, EcowittReading.Pm25, sensor.Channel);
        foreach (var sensor in LeakSensors)
            sensor.Sources = SelectSources(sensors, sensor.Sources, EcowittReading.Leak, sensor.Channel);
    }

    /// <returns>The current sources when unchanged, else a new dictionary with the sensors that feed the reading.</returns>
    private static Dictionary<string, EcowittSensor> SelectSources(
        Dictionary<string, EcowittSensor> sensors, Dictionary<string, EcowittSensor> current, EcowittReading reading, int channel)
    {
        var count = 0;
        var isChanged = false;
        foreach (var (sensorId, sensor) in sensors)
        {
            if (!IsSource(sensor.TypeCode, reading, channel))
                continue;

            count++;
            if (!current.TryGetValue(sensorId, out var existing) || !ReferenceEquals(existing, sensor))
                isChanged = true;
        }

        if (!isChanged && count == current.Count)
            return current;

        var next = new Dictionary<string, EcowittSensor>(count, StringComparer.Ordinal);
        foreach (var (sensorId, sensor) in sensors)
        {
            if (IsSource(sensor.TypeCode, reading, channel))
                next[sensorId] = sensor;
        }

        return next;
    }

    private static bool IsSource(int typeCode, EcowittReading reading, int channel) =>
        EcowittSensorTypes.TryGetChannelSlot(typeCode, out var sensorReading, out var sensorChannel)
            ? sensorReading == reading && sensorChannel == channel
            : EcowittSensorTypes.Feeds(typeCode, reading);

    // The persisted Rain*LastMonthlyValue holds the last reading of the item chosen here (0x14, else 0x13).
    // Choosing another item reads as a bucket reset or a huge increase and makes TotalRain jump.
    private static decimal? GetLongestRainBucket(EcowittRainData data)
        => data.TotalRain ?? data.YearlyRain;

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
        gauge.Last24HoursRain = data.Last24HoursRain;
        gauge.DailyRain = data.DailyRain;
        gauge.WeeklyRain = data.WeeklyRain;
        gauge.MonthlyRain = data.MonthlyRain;
        gauge.YearlyRain = data.YearlyRain;
        gauge.TotalRain = cumulativeTotal;
        gauge.LastUpdated = now;
    }

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
            ChannelSensors[i].Name = visible[i].Name;
            ChannelSensors[i].Temperature = visible[i].Temperature;
            ChannelSensors[i].Humidity = visible[i].Humidity;
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
            LeakSensors[i].LastUpdated = now;
        }
    }

    private bool IsSensorHidden(string sensorKey) =>
        _hiddenSensors.Contains(sensorKey, StringComparer.OrdinalIgnoreCase);

    internal void ResetState()
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
        if (Sensors.Count > 0)
            Sensors = new Dictionary<string, EcowittSensor>(StringComparer.Ordinal);
    }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default)
    {
        try { _configChangedSignal.Release(); }
        catch (SemaphoreFullException) { }
        return Task.CompletedTask;
    }
}
