// Register map: AIT SHI manual 83026900aDE; firmware gates: python-luxtronik 02afea84bd5bf3ee87445de6f2a42b8029983169.
using HomeBlaze.Abstractions.Attributes;
using Namotion.Devices.Luxtronik.Attributes;
using Namotion.Devices.Luxtronik.Enums;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Modbus;

namespace Namotion.Devices.Luxtronik.Model;

/// <summary>
/// Heating: the heating circuit, its return temperature control and its counters. Always present.
/// </summary>
[InterceptorSubject]
public partial class LuxtronikHeating
{
    /// <summary>
    /// Initializes the registers as unknown (<c>null</c>) until they are read.
    /// </summary>
    public LuxtronikHeating()
    {
        ExternalReturn = new LuxtronikTemperatureSensor(10102, "External return temperature");
        CirculationPump = new LuxtronikPump(10354, "Heating circulation pump (HUP)");
        SmartHomeControl = new LuxtronikSmartHomeControl(10000, LuxtronikFunction.None);
        OverallSmartHomeControl = new LuxtronikOverallSmartHomeControl();
        Status = null;
        ReturnTarget = null;
        MinimumReturnTarget = null;
        ReturnLimit = null;
        LimitTemperature = null;
        CalculatedFlowTemperature = null;
        IsLocked = null;
        TotalOperatingHours = null;
        TotalElectricalEnergy = null;
        TotalThermalEnergy = null;
    }

    /// <summary>
    /// Gets the state of heating.
    /// </summary>
    [LuxtronikInputRegister(10003, ModbusDataType.U16)]
    [State(IsDiscrete = true, Position = 1)]
    public partial LuxtronikModeStatus? Status { get; internal set; }

    /// <summary>
    /// Gets the return target temperature.
    /// </summary>
    [LuxtronikInputRegister(10101, ModbusDataType.U16, Scale = 0.1)]
    [State(Unit = StateUnit.DegreeCelsius, Position = 2)]
    public partial decimal? ReturnTarget { get; internal set; }

    /// <summary>
    /// Gets the minimum return target temperature.
    /// </summary>
    [LuxtronikInputRegister(10104, ModbusDataType.S16, Scale = 0.1)]
    [State(Unit = StateUnit.DegreeCelsius, Position = 3)]
    public partial decimal? MinimumReturnTarget { get; internal set; }

    /// <summary>
    /// Gets the return temperature limit, the maximum return target in heating.
    /// </summary>
    [LuxtronikInputRegister(10103, ModbusDataType.S16, Scale = 0.1)]
    [State(Unit = StateUnit.DegreeCelsius, Position = 4)]
    public partial decimal? ReturnLimit { get; internal set; }

    /// <summary>
    /// Gets the heating limit temperature (manual: Grenztemperatur Heizung): a return temperature above it counts as optional heating demand, below it as basic demand.
    /// </summary>
    [LuxtronikInputRegister(10107, ModbusDataType.S16, Scale = 0.1)]
    [State(Unit = StateUnit.DegreeCelsius, Position = 5)]
    public partial decimal? LimitTemperature { get; internal set; }

    /// <summary>
    /// Gets the calculated flow temperature, the return target plus the spread.
    /// </summary>
    [LuxtronikInputRegister(10113, ModbusDataType.S16, Scale = 0.1, MinimumFirmware = "3.92.0")]
    [State(Unit = StateUnit.DegreeCelsius, Position = 6)]
    public partial decimal? CalculatedFlowTemperature { get; internal set; }

    /// <summary>
    /// Gets whether heating is locked over the SHI.
    /// </summary>
    [LuxtronikHoldingRegister(10050, ModbusDataType.U16, MinimumFirmware = "3.92.0")]
    [State(IsDiscrete = true, Position = 7)]
    public partial bool? IsLocked { get; internal set; }

    /// <summary>
    /// Gets the operating hours in heating.
    /// </summary>
    [LuxtronikInputRegister(10406, ModbusDataType.U32, MinimumFirmware = "3.92.0")]
    [State(Unit = StateUnit.Hour, IsCumulative = true, Position = 8)]
    public partial decimal? TotalOperatingHours { get; internal set; }

    /// <summary>
    /// Gets the electrical energy consumed for heating.
    /// </summary>
    [LuxtronikInputRegister(10312, ModbusDataType.S32, Scale = 100)]
    [State(Unit = StateUnit.WattHour, IsCumulative = true, Position = 9)]
    public partial decimal? TotalElectricalEnergy { get; internal set; }

    /// <summary>
    /// Gets the thermal energy produced for heating.
    /// </summary>
    [LuxtronikInputRegister(10322, ModbusDataType.S32, Scale = 100, MinimumFirmware = "3.92.0")]
    [State(Unit = StateUnit.WattHour, IsCumulative = true, Position = 10)]
    public partial decimal? TotalThermalEnergy { get; internal set; }

    /// <summary>
    /// Gets the measured return temperature in a separation or multifunction tank.
    /// </summary>
    [State(Position = 20)]
    public partial LuxtronikTemperatureSensor ExternalReturn { get; internal set; }

    /// <summary>
    /// Gets the heating circulation pump output (HUP).
    /// </summary>
    [State(Position = 21)]
    public partial LuxtronikPump CirculationPump { get; internal set; }

    /// <summary>
    /// Gets the heating setpoint configuration sent over the SHI.
    /// </summary>
    [State(Position = 22)]
    public partial LuxtronikSmartHomeControl SmartHomeControl { get; internal set; }

    /// <summary>
    /// Gets the overall setpoint configuration for the heating circuit and all mixing circuits.
    /// </summary>
    [State(Position = 23)]
    public partial LuxtronikOverallSmartHomeControl OverallSmartHomeControl { get; internal set; }
}
