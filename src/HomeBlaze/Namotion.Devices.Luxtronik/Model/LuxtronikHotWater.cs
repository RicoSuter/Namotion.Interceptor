// Register map: AIT SHI manual 83026900aDE; firmware gates: python-luxtronik 02afea84bd5bf3ee87445de6f2a42b8029983169.
using HomeBlaze.Abstractions.Attributes;
using Namotion.Devices.Luxtronik.Attributes;
using Namotion.Devices.Luxtronik.Enums;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Modbus;

namespace Namotion.Devices.Luxtronik.Model;

/// <summary>
/// Hot water: the tank temperature, its targets, pumps, requests and counters. Always present, because the tank temperature is read even while hot water is off.
/// </summary>
[InterceptorSubject]
public partial class LuxtronikHotWater
{
    /// <summary>
    /// Initializes the registers as unknown (<c>null</c>) until they are read.
    /// </summary>
    public LuxtronikHotWater()
    {
        Temperature = new LuxtronikTemperatureSensor(10120, "Hot water temperature");
        LoadingPump = new LuxtronikPump(10355, "Hot water loading pump (BUP)");
        CirculationPump = new LuxtronikPump(10356, "Hot water circulation pump (ZIP)");
        SmartHomeControl = new LuxtronikSmartHomeControl(10005, LuxtronikFunction.None);
        ExtraHotWater = new LuxtronikExtraHotWater();
        Status = null;
        Target = null;
        MinimumTarget = null;
        MaximumTarget = null;
        LimitTemperature = null;
        IsLocked = null;
        IsCirculationRequested = null;
        OperatingHours = null;
        ElectricalEnergy = null;
        ThermalEnergy = null;
    }

    /// <summary>
    /// Gets the state of hot water.
    /// </summary>
    [LuxtronikInputRegister(10004, ModbusDataType.U16)]
    [State(IsDiscrete = true, Position = 1)]
    public partial LuxtronikModeStatus? Status { get; internal set; }

    /// <summary>
    /// Gets the hot water target temperature.
    /// </summary>
    [LuxtronikInputRegister(10121, ModbusDataType.U16, Scale = 0.1)]
    [State(Unit = StateUnit.DegreeCelsius, Position = 2)]
    public partial decimal? Target { get; internal set; }

    /// <summary>
    /// Gets the minimum hot water target; lower targets are not accepted.
    /// </summary>
    [LuxtronikInputRegister(10122, ModbusDataType.S16, Scale = 0.1)]
    [State(Unit = StateUnit.DegreeCelsius, Position = 3)]
    public partial decimal? MinimumTarget { get; internal set; }

    /// <summary>
    /// Gets the maximum hot water target.
    /// </summary>
    [LuxtronikInputRegister(10123, ModbusDataType.S16, Scale = 0.1)]
    [State(Unit = StateUnit.DegreeCelsius, Position = 4)]
    public partial decimal? MaximumTarget { get; internal set; }

    /// <summary>
    /// Gets the hot water limit temperature (manual: Grenztemperatur Warmwasser); below it the heat pump ignores a soft power limit.
    /// </summary>
    [LuxtronikInputRegister(10124, ModbusDataType.S16, Scale = 0.1)]
    [State(Unit = StateUnit.DegreeCelsius, Position = 5)]
    public partial decimal? LimitTemperature { get; internal set; }

    /// <summary>
    /// Gets whether hot water is locked over the SHI.
    /// </summary>
    [LuxtronikHoldingRegister(10051, ModbusDataType.U16, MinimumFirmware = "3.92.0")]
    [State(IsDiscrete = true, Position = 6)]
    public partial bool? IsLocked { get; internal set; }

    /// <summary>
    /// Gets whether a circulation run is requested over the SHI.
    /// </summary>
    [LuxtronikHoldingRegister(10070, ModbusDataType.U16, MinimumFirmware = "3.92.0")]
    [State(IsDiscrete = true, Position = 7)]
    public partial bool? IsCirculationRequested { get; internal set; }

    /// <summary>
    /// Gets the operating hours in hot water.
    /// </summary>
    [LuxtronikInputRegister(10408, ModbusDataType.U32, MinimumFirmware = "3.92.0")]
    [State(Unit = StateUnit.Hour, IsCumulative = true, Position = 8)]
    public partial decimal? OperatingHours { get; internal set; }

    /// <summary>
    /// Gets the electrical energy consumed for hot water.
    /// </summary>
    [LuxtronikInputRegister(10314, ModbusDataType.S32, Scale = 100)]
    [State(Unit = StateUnit.WattHour, IsCumulative = true, Position = 9)]
    public partial decimal? ElectricalEnergy { get; internal set; }

    /// <summary>
    /// Gets the thermal energy produced for hot water.
    /// </summary>
    [LuxtronikInputRegister(10324, ModbusDataType.S32, Scale = 100, MinimumFirmware = "3.92.0")]
    [State(Unit = StateUnit.WattHour, IsCumulative = true, Position = 10)]
    public partial decimal? ThermalEnergy { get; internal set; }

    /// <summary>
    /// Gets the measured hot water temperature.
    /// </summary>
    /// <remarks>
    /// Reports 75.0 °C when the hot water sensor fails: the controller substitutes this value and it is not filtered.
    /// </remarks>
    [State(Position = 20)]
    public partial LuxtronikTemperatureSensor Temperature { get; internal set; }

    /// <summary>
    /// Gets the hot water loading pump or changeover valve output (BUP).
    /// </summary>
    [State(Position = 21)]
    public partial LuxtronikPump LoadingPump { get; internal set; }

    /// <summary>
    /// Gets the hot water circulation pump output (ZIP).
    /// </summary>
    [State(Position = 22)]
    public partial LuxtronikPump CirculationPump { get; internal set; }

    /// <summary>
    /// Gets the hot water setpoint configuration sent over the SHI.
    /// </summary>
    [State(Position = 23)]
    public partial LuxtronikSmartHomeControl SmartHomeControl { get; internal set; }

    /// <summary>
    /// Gets the extra hot water request and its state.
    /// </summary>
    [State(Position = 24)]
    public partial LuxtronikExtraHotWater ExtraHotWater { get; internal set; }
}
