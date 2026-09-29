// Register map: AIT SHI manual 83026900aDE; firmware gates: python-luxtronik 02afea84bd5bf3ee87445de6f2a42b8029983169.
using HomeBlaze.Abstractions.Attributes;
using Namotion.Devices.Luxtronik.Attributes;
using Namotion.Devices.Luxtronik.Enums;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Modbus;

namespace Namotion.Devices.Luxtronik.Model;

/// <summary>
/// Operating state of the heat pump as a whole: heat generators, current operation, error, cycling times, operating hours and the brine pump.
/// </summary>
[InterceptorSubject]
public partial class LuxtronikOperatingStatus
{
    /// <summary>
    /// Initializes the registers as unknown (<c>null</c>) until they are read.
    /// </summary>
    public LuxtronikOperatingStatus()
    {
        BrinePump = new LuxtronikPump(10350, "Brine circulation pump (VBO)");
        HeatPumpStatus = null;
        OperatingState = null;
        ErrorNumber = null;
        BufferType = null;
        MinimumOffTime = null;
        MinimumRunTime = null;
        OperatingHours = null;
    }

    /// <summary>
    /// Gets the running compressors and auxiliary heaters.
    /// </summary>
    [LuxtronikInputRegister(10000, ModbusDataType.U16)]
    [State(IsDiscrete = true, Position = 1)]
    public partial LuxtronikHeatPumpStatus? HeatPumpStatus { get; internal set; }

    /// <summary>
    /// Gets what the heat pump currently does (manual: Betriebszustand).
    /// </summary>
    [LuxtronikInputRegister(10002, ModbusDataType.U16)]
    [State(IsDiscrete = true, Position = 2)]
    public partial LuxtronikOperatingState? OperatingState { get; internal set; }

    /// <summary>
    /// Gets the controller error number, 0 when there is no error.
    /// </summary>
    [LuxtronikInputRegister(10201, ModbusDataType.U16)]
    [State(IsDiscrete = true, Position = 3)]
    public partial ushort? ErrorNumber { get; internal set; }

    /// <summary>
    /// Gets the configured buffer tank type.
    /// </summary>
    [LuxtronikInputRegister(10202, ModbusDataType.U16)]
    [State(IsDiscrete = true, Position = 4)]
    public partial LuxtronikBufferType? BufferType { get; internal set; }

    /// <summary>
    /// Gets the minimum time the heat pump stays off before it may start again (cycling lock).
    /// </summary>
    [LuxtronikInputRegister(10203, ModbusDataType.U16)]
    [State(Unit = StateUnit.Minute, Position = 5)]
    public partial int? MinimumOffTime { get; internal set; }

    /// <summary>
    /// Gets the minimum time the heat pump runs once started.
    /// </summary>
    [LuxtronikInputRegister(10204, ModbusDataType.U16)]
    [State(Unit = StateUnit.Minute, Position = 6)]
    public partial int? MinimumRunTime { get; internal set; }

    /// <summary>
    /// Gets the operating hours of the heat pump.
    /// </summary>
    [LuxtronikInputRegister(10404, ModbusDataType.U32, MinimumFirmware = "3.92.0")]
    [State(Unit = StateUnit.Hour, IsCumulative = true, Position = 7)]
    public partial decimal? OperatingHours { get; internal set; }

    /// <summary>
    /// Gets the brine, well or fan pump output (VBO).
    /// </summary>
    [State(Position = 8)]
    public partial LuxtronikPump BrinePump { get; internal set; }

    /// <summary>
    /// Gets whether a compressor is running, or <c>null</c> when <see cref="HeatPumpStatus"/> is unknown.
    /// </summary>
    [Derived]
    [State(IsDiscrete = true, Position = 20)]
    public bool? IsCompressorRunning => HeatPumpStatus is { } status
        ? (status & (LuxtronikHeatPumpStatus.Compressor1 | LuxtronikHeatPumpStatus.Compressor2)) != 0
        : null;

    /// <summary>
    /// Gets whether an auxiliary heater is running, or <c>null</c> when <see cref="HeatPumpStatus"/> is unknown.
    /// </summary>
    [Derived]
    [State(IsDiscrete = true, Position = 21)]
    public bool? IsAuxiliaryHeaterRunning => HeatPumpStatus is { } status
        ? (status & (LuxtronikHeatPumpStatus.AuxiliaryHeater1 | LuxtronikHeatPumpStatus.AuxiliaryHeater2 | LuxtronikHeatPumpStatus.AuxiliaryHeater3)) != 0
        : null;
}
