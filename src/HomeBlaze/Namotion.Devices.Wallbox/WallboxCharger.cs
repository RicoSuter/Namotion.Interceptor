using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Common;
using HomeBlaze.Abstractions.Devices.Energy;
using HomeBlaze.Abstractions.Networking;
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

    // IConnectionState

    [State(IsDiscrete = true)]
    public partial bool IsConnected { get; internal set; }

    // IMonitoredService

    [State(IsDiscrete = true)]
    public partial ServiceStatus Status { get; internal set; }

    [State]
    public partial string? StatusMessage { get; internal set; }

    // ILastUpdatedProvider

    [State]
    public partial DateTimeOffset? LastUpdated { get; internal set; }

    // IEnergyChargerState

    [State(IsDiscrete = true)]
    public partial bool? IsPluggedIn { get; internal set; }

    [State(IsDiscrete = true)]
    public partial bool? IsCharging { get; internal set; }

    [State(Unit = StateUnit.Ampere)]
    public partial decimal? MaxChargingCurrent { get; internal set; }

    // IVehicleChargerState

    [State(Unit = StateUnit.Percent)]
    public partial decimal? ChargeLevel { get; internal set; }

    [State(Unit = StateUnit.Watt)]
    public partial decimal? ChargingPower { get; internal set; }

    // ISoftwareState

    [State]
    public partial string? SoftwareVersion { get; internal set; }

    [State]
    public partial string? AvailableSoftwareUpdate { get; internal set; }

    // IDeviceInfo

    [Derived]
    public string Manufacturer => "Wallbox";

    [State]
    public partial string? Model { get; internal set; }

    [State]
    public partial string? ProductCode { get; internal set; }

    // SerialNumber is already a Configuration property above
    string? IDeviceInfo.SerialNumber => SerialNumber;

    [State]
    public partial string? HardwareRevision { get; internal set; }

    // Device-specific state

    [State(IsDiscrete = true)]
    public partial WallboxChargerStatus ChargerStatus { get; internal set; }

    [State(IsDiscrete = true)]
    public partial bool? IsLocked { get; internal set; }

    [State(Unit = StateUnit.Ampere)]
    public partial decimal? ChargingSpeed { get; internal set; }

    [State]
    public partial decimal? AddedRange { get; internal set; }

    [State(Unit = StateUnit.KiloWattHour)]
    public partial decimal? AddedEnergy { get; internal set; }

    [State(Unit = StateUnit.KiloWattHour)]
    public partial decimal? AddedGreenEnergy { get; internal set; }

    [State(Unit = StateUnit.KiloWattHour)]
    public partial decimal? AddedGridEnergy { get; internal set; }

    [State]
    public partial TimeSpan? ChargingTime { get; internal set; }

    [State]
    public partial decimal? SessionCost { get; internal set; }

    [State]
    public partial decimal? EnergyPrice { get; internal set; }

    [State]
    public partial string? Currency { get; internal set; }

    [State(Unit = StateUnit.WattHour, IsCumulative = true)]
    public partial decimal? TotalEnergyConsumed { get; internal set; }

    [State(IsDiscrete = true)]
    public partial bool? EcoSmartEnabled { get; internal set; }

    [State(IsDiscrete = true)]
    public partial WallboxEcoSmartMode? EcoSmartMode { get; internal set; }

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
        IsPluggedIn = null;
        IsCharging = null;
        MaxChargingCurrent = null;
        ChargeLevel = null;
        ChargingPower = null;
        SoftwareVersion = null;
        AvailableSoftwareUpdate = null;
        Model = null;
        ProductCode = null;
        HardwareRevision = null;
        ChargerStatus = WallboxChargerStatus.Unknown;
        IsLocked = null;
        ChargingSpeed = null;
        AddedRange = null;
        AddedEnergy = null;
        AddedGreenEnergy = null;
        AddedGridEnergy = null;
        ChargingTime = null;
        SessionCost = null;
        EnergyPrice = null;
        Currency = null;
        TotalEnergyConsumed = null;
        EcoSmartEnabled = null;
        EcoSmartMode = null;
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

    [Operation(Title = "Set Max Charging Current")]
    public async Task SetMaxChargingCurrentAsync(int amperes, CancellationToken cancellationToken)
    {
        if (_client is not null && amperes >= 6 && amperes <= 32)
        {
            await _client.SetMaxChargingCurrentAsync(SerialNumber, amperes, cancellationToken);
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

    [Operation(Title = "Set ICP Max Current")]
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
        if (ChargerStatus == WallboxChargerStatus.Paused)
            await ResumeChargingAsync(cancellationToken);
    }

    [Operation(Title = "Unlock Charge Port")]
    public async Task UnlockChargePortAsync(CancellationToken cancellationToken)
    {
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

        // Map status response to subject properties
        ChargerStatus = status.Status;
        IsPluggedIn = !status.Finished;
        IsCharging = status.ChargingPowerInKw > 1;
        ChargingPower = status.ChargingPowerInKw * 1000m;
        ChargingSpeed = status.ChargingSpeed;
        ChargeLevel = status.StateOfCharge.HasValue ? status.StateOfCharge.Value / 100m : null;
        AddedRange = status.AddedRange;
        AddedEnergy = status.AddedEnergy;
        AddedGreenEnergy = status.AddedGreenEnergy;
        AddedGridEnergy = status.AddedGridEnergy;
        ChargingTime = TimeSpan.FromSeconds(status.ChargingTime);
        SessionCost = status.Cost;

        IsLocked = status.ConfigData?.Locked switch
        {
            1 => true,
            0 => false,
            _ => null
        };

        MaxChargingCurrent = status.ConfigData?.MaxChargingCurrent;
        EnergyPrice = status.ConfigData?.EnergyPrice;
        Currency = status.ConfigData?.Currency?.Symbol;
        ProductCode = status.ConfigData?.PartNumber;
        Model = MapPartNumberToModel(status.ConfigData?.PartNumber);
        HardwareRevision = null; // Not available from API

        SoftwareVersion = status.ConfigData?.Software?.CurrentVersion;
        AvailableSoftwareUpdate = status.ConfigData?.Software?.UpdateAvailable == true
            ? status.ConfigData.Software.LatestVersion
            : null;

        EcoSmartEnabled = status.ConfigData?.Ecosmart?.Enabled;
        EcoSmartMode = status.ConfigData?.Ecosmart is { } eco
            ? eco.Enabled ? (WallboxEcoSmartMode)eco.Mode : WallboxEcoSmartMode.Disabled
            : null;

        // Session energy aggregation (cached, refreshed every 30 min when plugged in)
        if (DateTimeOffset.UtcNow > _lastSessionsRetrieval.AddMinutes(30) &&
            status.ConfigData?.GroupId is > 0 &&
            status.ConfigData?.ChargerId is > 0)
        {
            try
            {
                var sessions = await _client.GetChargingSessionsAsync(
                    status.ConfigData.GroupId,
                    status.ConfigData.ChargerId,
                    _lastSessionsRetrieval == DateTimeOffset.MinValue ? DateTimeOffset.MinValue : _lastSessionsRetrieval,
                    DateTimeOffset.UtcNow,
                    cancellationToken);

                _cachedSessionEnergy += sessions.Sum(s => s.Attributes?.Energy ?? 0);
                _lastSessionsRetrieval = DateTimeOffset.UtcNow;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Failed to retrieve charging sessions");
            }
        }

        TotalEnergyConsumed = _cachedSessionEnergy +
            (IsPluggedIn == true ? status.AddedEnergy * 1000 : 0);

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
}
