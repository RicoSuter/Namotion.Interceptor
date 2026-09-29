// Register map: AIT SHI manual 83026900aDE.
using HomeBlaze.Abstractions.Attributes;
using Namotion.Devices.Luxtronik.Attributes;
using Namotion.Devices.Luxtronik.Enums;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Modbus;

namespace Namotion.Devices.Luxtronik.Model;

/// <summary>
/// Operating state of the heat pump (inputs 10000 to 10007 and 10201 to 10207).
/// </summary>
[InterceptorSubject]
public partial class LuxtronikOperatingStatus : IModbusBaseAddressProvider
{
    /// <summary>
    /// Initializes the registers as unknown (<c>null</c>) until they are read.
    /// </summary>
    public LuxtronikOperatingStatus()
    {
        HeatPumpStatus = null;
        OperationMode = null;
        HeatingStatus = null;
        HotWaterStatus = null;
        CoolingStatus = null;
        PoolHeatingStatus = null;
        ErrorCode = null;
        BufferType = null;
        MinimumOffTime = null;
        MinimumRunTime = null;
        CoolingReleased = null;
    }

    /// <inheritdoc />
    public int BaseAddress => 10000;

    /// <summary>
    /// Gets the running compressors and auxiliary heaters.
    /// </summary>
    [LuxtronikInputRegister(0, ModbusDataType.U16)]
    [State(IsDiscrete = true, Position = 1)]
    public partial LuxtronikHeatPumpStatus? HeatPumpStatus { get; internal set; }

    /// <summary>
    /// Gets the current operation of the heat pump.
    /// </summary>
    [LuxtronikInputRegister(2, ModbusDataType.U16)]
    [State(IsDiscrete = true, Position = 2)]
    public partial LuxtronikOperatingState? OperationMode { get; internal set; }

    /// <summary>
    /// Gets the state of heating.
    /// </summary>
    [LuxtronikInputRegister(3, ModbusDataType.U16)]
    [State(IsDiscrete = true, Position = 3)]
    public partial LuxtronikModeStatus? HeatingStatus { get; internal set; }

    /// <summary>
    /// Gets the state of hot water.
    /// </summary>
    [LuxtronikInputRegister(4, ModbusDataType.U16)]
    [State(IsDiscrete = true, Position = 4)]
    public partial LuxtronikModeStatus? HotWaterStatus { get; internal set; }

    /// <summary>
    /// Gets the state of cooling.
    /// </summary>
    [LuxtronikInputRegister(6, ModbusDataType.U16, Feature = LuxtronikFeature.Cooling)]
    [State(IsDiscrete = true, Position = 5)]
    public partial LuxtronikModeStatus? CoolingStatus { get; internal set; }

    /// <summary>
    /// Gets the state of pool heating.
    /// </summary>
    [LuxtronikInputRegister(7, ModbusDataType.U16, Feature = LuxtronikFeature.Pool)]
    [State(IsDiscrete = true, Position = 6)]
    public partial LuxtronikModeStatus? PoolHeatingStatus { get; internal set; }

    /// <summary>
    /// Gets the controller error number, 0 when there is no error.
    /// </summary>
    [LuxtronikInputRegister(201, ModbusDataType.U16)]
    [State(IsDiscrete = true, Position = 7)]
    public partial ushort? ErrorCode { get; internal set; }

    /// <summary>
    /// Gets the configured buffer tank type.
    /// </summary>
    [LuxtronikInputRegister(202, ModbusDataType.U16)]
    [State(IsDiscrete = true, Position = 8)]
    public partial LuxtronikBufferType? BufferType { get; internal set; }

    /// <summary>
    /// Gets the minimum time the heat pump stays off before it may start again (cycling lock).
    /// </summary>
    [LuxtronikInputRegister(203, ModbusDataType.U16)]
    [State(Unit = StateUnit.Minute, Position = 9)]
    public partial int? MinimumOffTime { get; internal set; }

    /// <summary>
    /// Gets the minimum time the heat pump runs once started.
    /// </summary>
    [LuxtronikInputRegister(204, ModbusDataType.U16)]
    [State(Unit = StateUnit.Minute, Position = 10)]
    public partial int? MinimumRunTime { get; internal set; }

    /// <summary>
    /// Gets whether cooling is released.
    /// </summary>
    [LuxtronikInputRegister(207, ModbusDataType.U16, Feature = LuxtronikFeature.Cooling)]
    [State(IsDiscrete = true, Position = 11)]
    public partial bool? CoolingReleased { get; internal set; }

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
