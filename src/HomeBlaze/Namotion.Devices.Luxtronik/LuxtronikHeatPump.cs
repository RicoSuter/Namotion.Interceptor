using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Common;
using HomeBlaze.Abstractions.Networking;
using HomeBlaze.Abstractions.Sensors;
using Microsoft.Extensions.Logging;
using Namotion.Devices.Luxtronik.Enums;
using Namotion.Devices.Luxtronik.Model;
using Namotion.Interceptor.Attributes;

namespace Namotion.Devices.Luxtronik;

/// <summary>
/// A Luxtronik 2.1 heat pump read through the Smart Home Interface (Modbus TCP, port 502). Read only.
/// </summary>
[Category("Devices")]
[Description("Luxtronik 2.1 heat pump (Alpha Innotec, Novelan) via the Smart Home Interface, read only")]
[InterceptorSubject]
public partial class LuxtronikHeatPump :
    IPowerSensor,
    IThermalPowerSensor,
    IConnectionState,
    ISoftwareState,
    IMonitoredService,
    ITitleProvider,
    IIconProvider,
    ILastUpdatedProvider
{
    private readonly ILogger<LuxtronikHeatPump> _logger;

    [Configuration]
    public partial string Name { get; set; }

    [Configuration]
    public partial string? HostAddress { get; set; }

    [Configuration]
    public partial int Port { get; set; }

    /// <summary>
    /// Gets or sets the interval between reads of the controller. Values below <see cref="MinimumPollingIntervalSeconds"/> are raised to it.
    /// </summary>
    [Configuration]
    public partial TimeSpan PollingInterval { get; set; }

    [State(IsDiscrete = true)]
    public partial bool IsConnected { get; internal set; }

    /// <summary>
    /// Gets the connection status. A non-zero controller error code is reported in <see cref="StatusMessage"/> while the
    /// connection is healthy, and the status stays <see cref="ServiceStatus.Running"/>.
    /// </summary>
    [State(IsDiscrete = true)]
    public partial ServiceStatus Status { get; internal set; }

    [State]
    public partial string? StatusMessage { get; internal set; }

    [State]
    public partial DateTimeOffset? LastUpdated { get; internal set; }

    /// <summary>
    /// Gets the controller firmware, such as "3.92.3", read by the discovery on every connect.
    /// </summary>
    [State]
    public partial string? SoftwareVersion { get; internal set; }

    [State(Position = 10)]
    public partial LuxtronikOperatingStatus OperatingStatus { get; internal set; }

    [State(Position = 11)]
    public partial LuxtronikTemperatures Temperatures { get; internal set; }

    [State(Position = 12)]
    public partial LuxtronikEnergy Energy { get; internal set; }

    [State(Position = 13)]
    public partial LuxtronikRuntime Runtime { get; internal set; }

    [State(Position = 14)]
    public partial LuxtronikOutputs Outputs { get; internal set; }

    [State(Position = 15)]
    public partial LuxtronikSmartGrid SmartGrid { get; internal set; }

    [State(Position = 16)]
    public partial LuxtronikExtraHotWater ExtraHotWater { get; internal set; }

    [State(Position = 17)]
    public partial LuxtronikFeatures Features { get; internal set; }

    [State(Position = 20)]
    public partial LuxtronikControl Heating { get; internal set; }

    [State(Position = 21)]
    public partial LuxtronikControl HotWater { get; internal set; }

    [State(Position = 22)]
    public partial LuxtronikMixingCircuit MixingCircuit1 { get; internal set; }

    [State(Position = 23)]
    public partial LuxtronikMixingCircuit MixingCircuit2 { get; internal set; }

    [State(Position = 24)]
    public partial LuxtronikMixingCircuit MixingCircuit3 { get; internal set; }

    [State(Position = 25)]
    public partial LuxtronikPowerLimit PowerLimit { get; internal set; }

    [State(Position = 26)]
    public partial LuxtronikLocks Locks { get; internal set; }

    [State(Position = 27)]
    public partial LuxtronikRoomControl RoomControl { get; internal set; }

    [State(Position = 28)]
    public partial LuxtronikOverallHeating OverallHeating { get; internal set; }

    [State(Position = 29)]
    public partial LuxtronikHotWaterRequests HotWaterRequests { get; internal set; }

    [Derived]
    public decimal? Power => Energy.ElectricalPower;

    [Derived]
    public decimal? EnergyConsumed => Energy.TotalElectricalEnergy;

    [Derived]
    public decimal? ThermalPower => Energy.HeatingPower;

    [Derived]
    public decimal? ThermalEnergyProduced => Energy.TotalThermalEnergy;

    [Derived]
    [State]
    public string? AvailableSoftwareUpdate => null;

    [Derived]
    public string? Title => string.IsNullOrEmpty(Name) ? "Luxtronik Heat Pump" : Name;

    public string? IconName => "HeatPump";

    [Derived]
    public string? IconColor => IsConnected ? "Success" : null;

    public LuxtronikHeatPump(ILogger<LuxtronikHeatPump> logger)
    {
        _logger = logger;

        Name = string.Empty;
        HostAddress = null;
        Port = 502;
        PollingInterval = TimeSpan.FromSeconds(5);

        IsConnected = false;
        Status = ServiceStatus.Stopped;
        StatusMessage = null;
        LastUpdated = null;
        SoftwareVersion = null;

        OperatingStatus = new LuxtronikOperatingStatus();
        Temperatures = new LuxtronikTemperatures();
        Energy = new LuxtronikEnergy();
        Runtime = new LuxtronikRuntime();
        Outputs = new LuxtronikOutputs();
        SmartGrid = new LuxtronikSmartGrid();
        ExtraHotWater = new LuxtronikExtraHotWater();
        Features = new LuxtronikFeatures();
        Heating = new LuxtronikControl(10000, LuxtronikFeature.None);
        HotWater = new LuxtronikControl(10005, LuxtronikFeature.None);
        MixingCircuit1 = new LuxtronikMixingCircuit(1);
        MixingCircuit2 = new LuxtronikMixingCircuit(2);
        MixingCircuit3 = new LuxtronikMixingCircuit(3);
        PowerLimit = new LuxtronikPowerLimit();
        Locks = new LuxtronikLocks();
        RoomControl = new LuxtronikRoomControl();
        OverallHeating = new LuxtronikOverallHeating();
        HotWaterRequests = new LuxtronikHotWaterRequests();
    }
}
