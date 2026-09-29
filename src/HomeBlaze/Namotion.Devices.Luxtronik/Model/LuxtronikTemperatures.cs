// Register map: AIT SHI manual 83026900aDE; firmware gates: python-luxtronik 02afea84bd5bf3ee87445de6f2a42b8029983169.
using HomeBlaze.Abstractions.Attributes;
using Namotion.Devices.Luxtronik.Attributes;
using Namotion.Devices.Luxtronik.Enums;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Modbus;

namespace Namotion.Devices.Luxtronik.Model;

/// <summary>
/// Temperatures (inputs 10100 to 10124). Measured values are sensor children; targets and limits are plain values.
/// </summary>
[InterceptorSubject]
public partial class LuxtronikTemperatures : IModbusBaseAddressProvider
{
    /// <summary>
    /// Initializes the registers as unknown (<c>null</c>) until they are read.
    /// </summary>
    public LuxtronikTemperatures()
    {
        Return = new LuxtronikTemperatureSensor(10100, "Return temperature");
        ExternalReturn = new LuxtronikTemperatureSensor(10102, "External return temperature");
        Flow = new LuxtronikTemperatureSensor(10105, "Flow temperature");
        Room = new LuxtronikTemperatureSensor(10106, "Room temperature", feature: LuxtronikFeature.RoomControlUnit);
        Outside = new LuxtronikTemperatureSensor(10108, "Outside temperature");
        OutsideAverage = new LuxtronikTemperatureSensor(10109, "Outside average temperature", "3.92.0");
        HeatSourceInlet = new LuxtronikTemperatureSensor(10110, "Heat source inlet temperature", "3.92.0");
        HeatSourceOutlet = new LuxtronikTemperatureSensor(10111, "Heat source outlet temperature", "3.92.0");
        HotWater = new LuxtronikTemperatureSensor(10120, "Hot water temperature");

        ReturnTarget = null;
        ReturnLimit = null;
        ReturnMinimumTarget = null;
        HeatingLimit = null;
        MaximumFlow = null;
        CalculatedFlow = null;
        HotWaterTarget = null;
        HotWaterMinimum = null;
        HotWaterMaximum = null;
        HotWaterLimit = null;
    }

    /// <inheritdoc />
    public int BaseAddress => 10100;

    /// <summary>
    /// Gets the measured return temperature.
    /// </summary>
    [State(Position = 1)]
    public partial LuxtronikTemperatureSensor Return { get; internal set; }

    /// <summary>
    /// Gets the measured external return temperature.
    /// </summary>
    [State(Position = 2)]
    public partial LuxtronikTemperatureSensor ExternalReturn { get; internal set; }

    /// <summary>
    /// Gets the measured flow temperature.
    /// </summary>
    [State(Position = 3)]
    public partial LuxtronikTemperatureSensor Flow { get; internal set; }

    /// <summary>
    /// Gets the room temperature measured by the room control unit.
    /// </summary>
    [State(Position = 4)]
    public partial LuxtronikTemperatureSensor Room { get; internal set; }

    /// <summary>
    /// Gets the measured outside temperature.
    /// </summary>
    [State(Position = 5)]
    public partial LuxtronikTemperatureSensor Outside { get; internal set; }

    /// <summary>
    /// Gets the average outside temperature.
    /// </summary>
    [State(Position = 6)]
    public partial LuxtronikTemperatureSensor OutsideAverage { get; internal set; }

    /// <summary>
    /// Gets the measured heat source inlet temperature.
    /// </summary>
    [State(Position = 7)]
    public partial LuxtronikTemperatureSensor HeatSourceInlet { get; internal set; }

    /// <summary>
    /// Gets the measured heat source outlet temperature.
    /// </summary>
    [State(Position = 8)]
    public partial LuxtronikTemperatureSensor HeatSourceOutlet { get; internal set; }

    /// <summary>
    /// Gets the measured hot water temperature.
    /// </summary>
    /// <remarks>
    /// Reports 75.0 °C when the hot water sensor fails: the controller substitutes this value and it is not filtered.
    /// </remarks>
    [State(Position = 9)]
    public partial LuxtronikTemperatureSensor HotWater { get; internal set; }

    /// <summary>
    /// Gets the return target temperature.
    /// </summary>
    [LuxtronikInputRegister(1, ModbusDataType.U16, Scale = 0.1)]
    [State(Unit = StateUnit.DegreeCelsius, Position = 20)]
    public partial decimal? ReturnTarget { get; internal set; }

    /// <summary>
    /// Gets the maximum return temperature.
    /// </summary>
    [LuxtronikInputRegister(3, ModbusDataType.S16, Scale = 0.1)]
    [State(Unit = StateUnit.DegreeCelsius, Position = 21)]
    public partial decimal? ReturnLimit { get; internal set; }

    /// <summary>
    /// Gets the minimum return target temperature.
    /// </summary>
    [LuxtronikInputRegister(4, ModbusDataType.S16, Scale = 0.1)]
    [State(Unit = StateUnit.DegreeCelsius, Position = 22)]
    public partial decimal? ReturnMinimumTarget { get; internal set; }

    /// <summary>
    /// Gets the heating limit: a return temperature above it counts as optional heating demand, below it as basic demand.
    /// </summary>
    [LuxtronikInputRegister(7, ModbusDataType.S16, Scale = 0.1)]
    [State(Unit = StateUnit.DegreeCelsius, Position = 23)]
    public partial decimal? HeatingLimit { get; internal set; }

    /// <summary>
    /// Gets the maximum flow temperature.
    /// </summary>
    [LuxtronikInputRegister(12, ModbusDataType.U16, Scale = 0.1, MinimumFirmware = "3.92.0")]
    [State(Unit = StateUnit.DegreeCelsius, Position = 24)]
    public partial decimal? MaximumFlow { get; internal set; }

    /// <summary>
    /// Gets the calculated flow temperature, the return target plus the spread.
    /// </summary>
    [LuxtronikInputRegister(13, ModbusDataType.S16, Scale = 0.1, MinimumFirmware = "3.92.0")]
    [State(Unit = StateUnit.DegreeCelsius, Position = 25)]
    public partial decimal? CalculatedFlow { get; internal set; }

    /// <summary>
    /// Gets the hot water target temperature.
    /// </summary>
    [LuxtronikInputRegister(21, ModbusDataType.U16, Scale = 0.1)]
    [State(Unit = StateUnit.DegreeCelsius, Position = 26)]
    public partial decimal? HotWaterTarget { get; internal set; }

    /// <summary>
    /// Gets the minimum hot water temperature.
    /// </summary>
    [LuxtronikInputRegister(22, ModbusDataType.S16, Scale = 0.1)]
    [State(Unit = StateUnit.DegreeCelsius, Position = 27)]
    public partial decimal? HotWaterMinimum { get; internal set; }

    /// <summary>
    /// Gets the maximum hot water temperature.
    /// </summary>
    [LuxtronikInputRegister(23, ModbusDataType.S16, Scale = 0.1)]
    [State(Unit = StateUnit.DegreeCelsius, Position = 28)]
    public partial decimal? HotWaterMaximum { get; internal set; }

    /// <summary>
    /// Gets the hot water limit temperature; below it the heat pump ignores a soft power limit.
    /// </summary>
    [LuxtronikInputRegister(24, ModbusDataType.S16, Scale = 0.1)]
    [State(Unit = StateUnit.DegreeCelsius, Position = 29)]
    public partial decimal? HotWaterLimit { get; internal set; }
}
