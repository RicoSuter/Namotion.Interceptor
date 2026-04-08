using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Common;
using HomeBlaze.Abstractions.Devices;
using HomeBlaze.Abstractions.Devices.Energy;
using HomeBlaze.Abstractions.Networking;
using HomeBlaze.Abstractions.Sensors;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Devices.Wallbox.Model;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;

namespace Namotion.Devices.Wallbox;

[Category("Devices")]
[Description("Wallbox EV charger (Pulsar, Commander, Quasar) via cloud API")]
[InterceptorSubject]
public partial class WallboxCharger : BackgroundService,
    IVehicleCharger,
    IConnectionState,
    ISoftwareState,
    IDeviceInfo,
    IPowerSensor,
    IConfigurable,
    IMonitoredService,
    ITitleProvider,
    IIconProvider,
    ILastUpdatedProvider
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<WallboxCharger> _logger;
    private readonly SemaphoreSlim _configChangedSignal = new(0, 1);

    private WallboxClient? _client;
    private DateTimeOffset _lastSessionsRetrieval = DateTimeOffset.MinValue;
    private readonly Dictionary<int, decimal> _yearlySessionEnergy = new();
    private decimal _cachedSessionEnergy;

    // Configuration

    [Configuration]
    public partial string Name { get; set; }

    [Configuration]
    public partial string Email { get; set; }

    [Configuration(IsSecret = true)]
    public partial string Password { get; set; }

    [Configuration]
    public partial string SerialNumber { get; set; }

    [Configuration]
    public partial TimeSpan PollingInterval { get; set; }

    [Configuration]
    public partial TimeSpan RetryInterval { get; set; }

    // Charging state

    [State(IsDiscrete = true, Position = 1)]
    public partial WallboxChargerStatus ChargerStatus { get; internal set; }

    [State(IsDiscrete = true, Position = 2)]
    public partial bool? IsPluggedIn { get; internal set; }

    [State(IsDiscrete = true, Position = 3)]
    public partial bool? IsCharging { get; internal set; }

    [State(Unit = StateUnit.Watt, Position = 4)]
    public partial decimal? ChargingPower { get; internal set; }

    // Workaround: Pulsar MAX firmware always returns charging_speed=0.
    // Derived from power and phases assuming 230V nominal (EU/Type 2 markets).
    [State(Unit = StateUnit.Ampere, IsEstimated = true, Position = 5)]
    public partial decimal? ChargingCurrent { get; internal set; }

    // max_available_power from the API is actually the hardware max current in amps
    [State(Unit = StateUnit.Ampere, Position = 6)]
    public partial decimal? MaximumAvailableChargingCurrent { get; internal set; }

    [State(Unit = StateUnit.Ampere, Position = 7)]
    public partial decimal? MaximumChargingCurrent { get; internal set; }

    [State(Unit = StateUnit.Watt, Position = 8)]
    public partial decimal? MaximumChargingPower { get; internal set; }

    [State(IsDiscrete = true, Position = 9)]
    public partial bool? IsLocked { get; internal set; }

    // IVehicleChargerState (ChargeLevel delegated to Session)

    [Derived]
    [State(Unit = StateUnit.Percent, Position = 10)]
    public decimal? ChargeLevel => Session.ChargeLevel;

    // Energy

    [State(Unit = StateUnit.WattHour, IsCumulative = true, Position = 12)]
    public partial decimal? TotalEnergyConsumed { get; internal set; }

    [State(Position = 13)]
    public partial decimal? EnergyPrice { get; internal set; }

    [State(Position = 14)]
    public partial string? Currency { get; internal set; }

    // Eco-Smart

    [State(IsDiscrete = true, Position = 15)]
    public partial bool? EcoSmartEnabled { get; internal set; }

    [State(IsDiscrete = true, Position = 16)]
    public partial WallboxEcoSmartMode? EcoSmartMode { get; internal set; }

    // IDeviceInfo

    [Derived]
    [State(Position = 19)]
    public string Manufacturer => "Wallbox";

    [State(Position = 20)]
    public partial string? Model { get; internal set; }

    [State(Position = 21)]
    public partial string? ProductCode { get; internal set; }

    [State(Position = 23)]
    public partial string? HardwareRevision { get; internal set; }

    // ISoftwareState

    [State(Position = 25)]
    public partial string? SoftwareVersion { get; internal set; }

    [State(Position = 26)]
    public partial string? AvailableSoftwareUpdate { get; internal set; }

    // Connection & service status

    [State(IsDiscrete = true, Position = 30)]
    public partial bool IsConnected { get; internal set; }

    [State(IsDiscrete = true, Position = 31)]
    public partial ServiceStatus Status { get; internal set; }

    [State(Position = 32)]
    public partial string? StatusMessage { get; internal set; }

    [State(Position = 33)]
    public partial DateTimeOffset? LastUpdated { get; internal set; }

    // Charging session (child subject)

    [State]
    public partial WallboxChargingSession Session { get; internal set; }

    // IPowerSensor (delegates to ChargingPower/TotalEnergyConsumed)

    [Derived]
    public decimal? Power => ChargingPower;

    [Derived]
    public decimal? EnergyConsumed => TotalEnergyConsumed;

    // Derived

    [Derived]
    public string? Title => !string.IsNullOrEmpty(Name) ? Name : SerialNumber;

    [Derived]
    public string IconName => "EvStation";

    [Derived]
    public string? IconColor => IsConnected switch
    {
        false => "Error",
        true => IsCharging == true ? "Success" : IsPluggedIn == true ? "Warning" : "Default"
    };

    // Operation enable conditions

    [Derived]
    [PropertyAttribute("LockAsync", KnownAttributes.IsEnabled)]
    public bool LockAsync_IsEnabled => IsConnected && IsLocked != true;

    [Derived]
    [PropertyAttribute("UnlockAsync", KnownAttributes.IsEnabled)]
    public bool UnlockAsync_IsEnabled => IsConnected && IsLocked == true;

    [Derived]
    [PropertyAttribute("PauseChargingAsync", KnownAttributes.IsEnabled)]
    public bool PauseChargingAsync_IsEnabled => IsConnected && IsCharging == true;

    [Derived]
    [PropertyAttribute("ResumeChargingAsync", KnownAttributes.IsEnabled)]
    public bool ResumeChargingAsync_IsEnabled => IsConnected && ChargerStatus == WallboxChargerStatus.Paused;

    // Constructor

    public WallboxCharger(IHttpClientFactory httpClientFactory, ILogger<WallboxCharger> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;

        Name = string.Empty;
        Email = string.Empty;
        Password = string.Empty;
        SerialNumber = string.Empty;
        PollingInterval = TimeSpan.FromSeconds(90);
        RetryInterval = TimeSpan.FromSeconds(60);

        IsConnected = false;
        Status = ServiceStatus.Stopped;
        StatusMessage = null;
        LastUpdated = null;
        ChargerStatus = WallboxChargerStatus.Unknown;
        IsPluggedIn = null;
        IsCharging = null;
        ChargingPower = null;
        ChargingCurrent = null;
        MaximumAvailableChargingCurrent = null;
        MaximumChargingCurrent = null;
        MaximumChargingPower = null;
        IsLocked = null;
        TotalEnergyConsumed = null;
        EnergyPrice = null;
        Currency = null;
        EcoSmartEnabled = null;
        EcoSmartMode = null;
        Model = null;
        ProductCode = null;
        HardwareRevision = null;
        SoftwareVersion = null;
        AvailableSoftwareUpdate = null;
        Session = new WallboxChargingSession();
    }

    // IVehicleChargerController

    [Operation(Title = "Pause Charging", Icon = "Pause")]
    public async Task PauseChargingAsync(CancellationToken cancellationToken)
    {
        if (_client is not null)
        {
            await _client.PauseAsync(SerialNumber, cancellationToken);
            await PollAsync(cancellationToken);
        }
    }

    [Operation(Title = "Resume Charging", Icon = "PlayArrow")]
    public async Task ResumeChargingAsync(CancellationToken cancellationToken)
    {
        if (_client is not null)
        {
            await _client.ResumeAsync(SerialNumber, cancellationToken);
            await PollAsync(cancellationToken);
        }
    }

    // Device-specific operations

    [Operation(Title = "Lock", Icon = "Lock")]
    public async Task LockAsync(CancellationToken cancellationToken)
    {
        if (_client is not null)
        {
            await _client.LockAsync(SerialNumber, cancellationToken);
            await PollAsync(cancellationToken);
        }
    }

    [Operation(Title = "Unlock", Icon = "LockOpen")]
    public async Task UnlockAsync(CancellationToken cancellationToken)
    {
        if (_client is not null)
        {
            await _client.UnlockAsync(SerialNumber, cancellationToken);
            await PollAsync(cancellationToken);
        }
    }

    [Operation(Title = "Set Maximum Charging Current", RequiresConfirmation = true)]
    public async Task SetMaximumChargingCurrentAsync(int amperes, CancellationToken cancellationToken)
    {
        if (_client is not null && amperes >= 6 && amperes <= 32)
        {
            await _client.SetMaximumChargingCurrentAsync(SerialNumber, amperes, cancellationToken);
            await PollAsync(cancellationToken);
        }
    }

    [Operation(Title = "Set Energy Price")]
    public async Task SetEnergyPriceAsync(decimal pricePerKwh, CancellationToken cancellationToken)
    {
        if (_client is not null)
        {
            await _client.SetEnergyPriceAsync(SerialNumber, pricePerKwh, cancellationToken);
            await PollAsync(cancellationToken);
        }
    }

    [Operation(Title = "Set ICP Max Current", RequiresConfirmation = true)]
    public async Task SetIcpMaxCurrentAsync(int amperes, CancellationToken cancellationToken)
    {
        if (_client is not null)
        {
            await _client.SetIcpMaxCurrentAsync(SerialNumber, amperes, cancellationToken);
            await PollAsync(cancellationToken);
        }
    }

    [Operation(Title = "Set Eco-Smart Mode")]
    public async Task SetEcoSmartAsync(WallboxEcoSmartMode mode, CancellationToken cancellationToken)
    {
        if (_client is not null)
        {
            await _client.SetEcoSmartAsync(SerialNumber, mode, cancellationToken);
            await PollAsync(cancellationToken);
        }
    }

    [Operation(Title = "Reboot", RequiresConfirmation = true)]
    public async Task RebootAsync(CancellationToken cancellationToken)
    {
        if (_client is not null)
        {
            await _client.RebootAsync(SerialNumber, cancellationToken);
        }
    }

    [Operation(Title = "Update Firmware", RequiresConfirmation = true)]
    public async Task UpdateFirmwareAsync(CancellationToken cancellationToken)
    {
        if (_client is not null)
        {
            await _client.UpdateFirmwareAsync(SerialNumber, cancellationToken);
        }
    }

    // Charge port lock operations (v1 parity)
    // Wallbox has no dedicated charge port lock API. The Type 2 connector locks
    // mechanically while a charging session is active and unlocks when paused.
    // These operations therefore toggle pause/resume to control the physical latch.

    [Operation(Title = "Toggle Charge Port Lock")]
    public async Task ToggleChargePortLockAsync(CancellationToken cancellationToken)
    {
        if (ChargerStatus == WallboxChargerStatus.Paused)
            await ResumeChargingAsync(cancellationToken);
        else
            await UnlockChargePortAsync(cancellationToken);
    }

    [Operation(Title = "Lock Charge Port")]
    public async Task LockChargePortAsync(CancellationToken cancellationToken)
    {
        // Resume charging to re-engage the connector latch
        if (ChargerStatus == WallboxChargerStatus.Paused)
            await ResumeChargingAsync(cancellationToken);
    }

    [Operation(Title = "Unlock Charge Port")]
    public async Task UnlockChargePortAsync(CancellationToken cancellationToken)
    {
        // The Type 2 connector latch disengages when the session is paused.
        // If actively charging, we pause to release the latch. If idle but plugged in
        // (e.g., WaitingForCarDemand), we resume first to start a session, which can
        // then be paused to release the latch. Tested with Tesla Model 3 (v1, 2024-08).
        if (ChargerStatus != WallboxChargerStatus.Paused)
        {
            if (IsCharging == true)
                await PauseChargingAsync(cancellationToken);
            else
                await ResumeChargingAsync(cancellationToken);
        }
    }

    // BackgroundService

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (string.IsNullOrEmpty(SerialNumber) || string.IsNullOrEmpty(Email) || string.IsNullOrEmpty(Password))
            {
                Status = ServiceStatus.Stopped;
                StatusMessage = "Missing configuration (email, password, or serial number)";
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

                _client = new WallboxClient(_httpClientFactory, Email, Password);
                await PollAsync(stoppingToken);

                Status = ServiceStatus.Running;
                StatusMessage = null;
                IsConnected = true;

                await RunPollingLoopAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Wallbox {SerialNumber} connection failed", SerialNumber);
                IsConnected = false;
                Status = ServiceStatus.Error;
                StatusMessage = exception.Message;

                await Task.Delay(RetryInterval, stoppingToken);
            }
        }

        Status = ServiceStatus.Stopped;
        StatusMessage = null;
    }

    private async Task RunPollingLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollAsync(stoppingToken);
                LastUpdated = DateTimeOffset.UtcNow;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Wallbox {SerialNumber} poll failed", SerialNumber);
                IsConnected = false;
                Status = ServiceStatus.Error;
                StatusMessage = exception.Message;
                return;
            }

            try
            {
                var signaled = await _configChangedSignal.WaitAsync(PollingInterval, stoppingToken);
                if (signaled)
                {
                    _client = null;
                    _lastSessionsRetrieval = DateTimeOffset.MinValue;
                    _yearlySessionEnergy.Clear();
                    _cachedSessionEnergy = 0;
                    return;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        if (_client is null)
            return;

        var status = await _client.GetChargerStatusAsync(SerialNumber, cancellationToken);

        // Charger status
        ChargerStatus = status.Status;

        // Only override Finished flag for definitive "not plugged" states;
        // all other statuses (including future ones) fall back to API's Finished flag.
        IsPluggedIn = ChargerStatus is WallboxChargerStatus.Disconnected or WallboxChargerStatus.Ready
            ? false
            : !status.Finished;

        IsCharging = ChargerStatus is WallboxChargerStatus.Charging or WallboxChargerStatus.Discharging;
        ChargingPower = status.ChargingPowerInKw * 1000m;

        // Workaround: Pulsar MAX firmware always returns charging_speed=0.
        // Derive current from power and phases assuming 230V nominal (EU/Type 2 markets).
        ChargingCurrent = status.ChargingCurrent > 0
            ? status.ChargingCurrent
            : status.CurrentMode > 0 && status.ChargingPowerInKw > 0
                ? Math.Round(status.ChargingPowerInKw * 1000m / (230m * status.CurrentMode), 1)
                : 0;

        // max_available_power from the API is actually the hardware max current in amps
        MaximumAvailableChargingCurrent = status.MaxAvailablePower > 0
            ? status.MaxAvailablePower
            : null;

        MaximumChargingCurrent = status.ConfigData?.MaximumChargingCurrent;

        MaximumChargingPower = status.MaxAvailablePower > 0 && status.CurrentMode > 0
            ? status.MaxAvailablePower * 230m * status.CurrentMode
            : null;

        IsLocked = status.ConfigData?.Locked switch
        {
            1 => true,
            0 => false,
            _ => null
        };

        // Current session
        Session.IsCharging = IsCharging == true;
        Session.ChargeLevel = status.StateOfCharge.HasValue ? status.StateOfCharge.Value / 100m : null;
        Session.AddedEnergy = status.AddedEnergy * 1000m;
        Session.AddedGreenEnergy = status.AddedGreenEnergy * 1000m;
        Session.AddedGridEnergy = status.AddedGridEnergy * 1000m;
        Session.AddedRange = status.AddedRange;
        Session.ChargingTime = TimeSpan.FromSeconds(status.ChargingTime);
        Session.SessionCost = status.AddedEnergy > 0 && status.Cost == 0 ? null : status.Cost;

        // Energy pricing
        EnergyPrice = status.ConfigData?.EnergyPrice;
        Currency = status.ConfigData?.Currency?.Symbol;

        // Eco-Smart
        EcoSmartEnabled = status.ConfigData?.Ecosmart?.Enabled;
        EcoSmartMode = status.ConfigData?.Ecosmart is { } eco
            ? eco.Enabled ? (WallboxEcoSmartMode)eco.Mode : WallboxEcoSmartMode.Disabled
            : null;

        // Device info
        ProductCode = status.ConfigData?.PartNumber;
        Model = MapPartNumberToModel(status.ConfigData?.PartNumber);
        HardwareRevision = null; // Not available from API

        // Software
        SoftwareVersion = status.ConfigData?.Software?.CurrentVersion;
        AvailableSoftwareUpdate = status.ConfigData?.Software?.UpdateAvailable == true
            ? status.ConfigData.Software.LatestVersion
            : null;

        // Session energy aggregation per year. Completed years are fetched once
        // and cached for the service lifetime. Only the current year is re-fetched
        // every 30 min. Session energy values are in Wh.
        if (DateTimeOffset.UtcNow > _lastSessionsRetrieval.AddMinutes(30) &&
            status.ConfigData?.GroupId is > 0 &&
            status.ConfigData?.ChargerId is > 0)
        {
            try
            {
                var currentYear = DateTimeOffset.UtcNow.Year;

                // Cache completed years (fetched once per service lifetime)
                for (var year = currentYear - 1; year >= 2019; year--)
                {
                    if (_yearlySessionEnergy.ContainsKey(year))
                        continue;

                    var yearStart = new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero);
                    var yearEnd = new DateTimeOffset(year + 1, 1, 1, 0, 0, 0, TimeSpan.Zero);
                    var sessions = await _client.GetChargingSessionsAsync(
                        status.ConfigData.GroupId, status.ConfigData.ChargerId,
                        yearStart, yearEnd, cancellationToken);
                    _yearlySessionEnergy[year] = sessions.Sum(s => s.Attributes?.Energy ?? 0);
                }

                // Always re-fetch current year
                {
                    var yearStart = new DateTimeOffset(currentYear, 1, 1, 0, 0, 0, TimeSpan.Zero);
                    var sessions = await _client.GetChargingSessionsAsync(
                        status.ConfigData.GroupId, status.ConfigData.ChargerId,
                        yearStart, DateTimeOffset.UtcNow, cancellationToken);
                    _yearlySessionEnergy[currentYear] = sessions.Sum(s => s.Attributes?.Energy ?? 0);
                }

                _cachedSessionEnergy = _yearlySessionEnergy.Values.Sum();
                _lastSessionsRetrieval = DateTimeOffset.UtcNow;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Failed to retrieve charging sessions");
            }
        }

        // AddedEnergy from status API is in kWh; add current session energy (not yet in sessions API)
        TotalEnergyConsumed = _cachedSessionEnergy +
            (IsPluggedIn == true ? status.AddedEnergy * 1000m : 0);

        IsConnected = true;
    }

    private static string? MapPartNumberToModel(string? partNumber)
    {
        if (string.IsNullOrEmpty(partNumber))
            return null;

        return partNumber[..3] switch
        {
            "PLP" => "Pulsar MAX",
            "PLM" => "Pulsar MAX",
            "PLS" => "Pulsar Plus",
            "CPB" => "Commander 2",
            "CPC" => "Commander 2",
            "QSA" => "Quasar",
            "QSB" => "Quasar 2",
            "SPB" => "Supernova",
            "CMX" => "Copper SB",
            _ => partNumber
        };
    }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default)
    {
        try { _configChangedSignal.Release(); }
        catch (SemaphoreFullException) { }
        return Task.CompletedTask;
    }

    public override void Dispose()
    {
        _configChangedSignal.Dispose();
        base.Dispose();
    }
}
