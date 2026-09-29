using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Common;
using HomeBlaze.Abstractions.Networking;
using HomeBlaze.Abstractions.Sensors;
using Microsoft.Extensions.Logging;
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
    /// Gets the connection status. A non-zero controller error number is reported in <see cref="StatusMessage"/> while the
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

    /// <summary>
    /// Gets the operating state of the heat pump as a whole.
    /// </summary>
    [State(Position = 10)]
    public partial LuxtronikOperatingStatus OperatingStatus { get; internal set; }

    /// <summary>
    /// Gets the temperatures of the heat pump itself.
    /// </summary>
    [State(Position = 11)]
    public partial LuxtronikTemperatures Temperatures { get; internal set; }

    /// <summary>
    /// Gets the power and energy totals.
    /// </summary>
    [State(Position = 12)]
    public partial LuxtronikEnergy Energy { get; internal set; }

    /// <summary>
    /// Gets the Smart Grid (EVU) signals.
    /// </summary>
    [State(Position = 13)]
    public partial LuxtronikSmartGrid SmartGrid { get; internal set; }

    /// <summary>
    /// Gets the electrical power consumption limit.
    /// </summary>
    [State(Position = 14)]
    public partial LuxtronikPowerConsumptionLimit PowerConsumptionLimit { get; internal set; }

    /// <summary>
    /// Gets which functions are active; the optional function subjects follow these flags.
    /// </summary>
    [State(Position = 15)]
    public partial LuxtronikFunctions Functions { get; internal set; }

    /// <summary>
    /// Gets heating.
    /// </summary>
    [State(Position = 20)]
    public partial LuxtronikHeating Heating { get; internal set; }

    /// <summary>
    /// Gets hot water.
    /// </summary>
    [State(Position = 21)]
    public partial LuxtronikHotWater HotWater { get; internal set; }

    /// <summary>
    /// Gets cooling, or <c>null</c> while its flag is clear.
    /// </summary>
    [State(Position = 22)]
    public partial LuxtronikCooling? Cooling { get; internal set; }

    /// <summary>
    /// Gets pool heating, or <c>null</c> while its flag is clear.
    /// </summary>
    [State(Position = 23)]
    public partial LuxtronikPool? Pool { get; internal set; }

    /// <summary>
    /// Gets solar, or <c>null</c> while its flag is clear.
    /// </summary>
    [State(Position = 24)]
    public partial LuxtronikSolar? Solar { get; internal set; }

    /// <summary>
    /// Gets the room control unit, or <c>null</c> while its flag is clear.
    /// </summary>
    [State(Position = 25)]
    public partial LuxtronikRoomControl? RoomControl { get; internal set; }

    /// <summary>
    /// Gets mixing circuit 1, or <c>null</c> while neither its heating nor its cooling flag is set.
    /// </summary>
    [State(Position = 26)]
    public partial LuxtronikMixingCircuit? MixingCircuit1 { get; internal set; }

    /// <summary>
    /// Gets mixing circuit 2, or <c>null</c> while neither its heating nor its cooling flag is set.
    /// </summary>
    [State(Position = 27)]
    public partial LuxtronikMixingCircuit? MixingCircuit2 { get; internal set; }

    /// <summary>
    /// Gets mixing circuit 3, or <c>null</c> while neither its heating nor its cooling flag is set.
    /// </summary>
    [State(Position = 28)]
    public partial LuxtronikMixingCircuit? MixingCircuit3 { get; internal set; }

    [Derived]
    public decimal? Power => Energy.ElectricalPower;

    [Derived]
    public decimal? EnergyConsumed => Energy.TotalElectricalEnergy;

    [Derived]
    public decimal? ThermalPower => Energy.ThermalPower;

    [Derived]
    public decimal? ThermalEnergyProduced => Energy.TotalThermalEnergy;

    [Derived]
    [State]
    public string? AvailableSoftwareUpdate => null;

    [Derived]
    public string Title => string.IsNullOrEmpty(Name) ? "Luxtronik Heat Pump" : Name;

    public string IconName => "HeatPump";

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
        SmartGrid = new LuxtronikSmartGrid();
        PowerConsumptionLimit = new LuxtronikPowerConsumptionLimit();
        Functions = new LuxtronikFunctions();
        Heating = new LuxtronikHeating();
        HotWater = new LuxtronikHotWater();
        Cooling = null;
        Pool = null;
        Solar = null;
        RoomControl = null;
        MixingCircuit1 = null;
        MixingCircuit2 = null;
        MixingCircuit3 = null;
    }
}
