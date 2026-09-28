// Register map: AIT SHI manual 83026900aDE.
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Modbus;

namespace Namotion.Devices.Luxtronik;

/// <summary>
/// Operating state of the heat pump (inputs 10000 to 10007 and 10201 to 10207).
/// </summary>
[InterceptorSubject]
public partial class LuxtronikOperatingStatus : IModbusBaseAddressProvider
{
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

    public int BaseAddress => 10000;

    [LuxtronikInputRegister(0, ModbusDataType.U16)]
    [State(IsDiscrete = true, Position = 1)]
    public partial LuxtronikHeatPumpStatus? HeatPumpStatus { get; internal set; }

    [LuxtronikInputRegister(2, ModbusDataType.U16)]
    [State(IsDiscrete = true, Position = 2)]
    public partial LuxtronikOperationMode? OperationMode { get; internal set; }

    [LuxtronikInputRegister(3, ModbusDataType.U16)]
    [State(IsDiscrete = true, Position = 3)]
    public partial LuxtronikModeStatus? HeatingStatus { get; internal set; }

    [LuxtronikInputRegister(4, ModbusDataType.U16)]
    [State(IsDiscrete = true, Position = 4)]
    public partial LuxtronikModeStatus? HotWaterStatus { get; internal set; }

    [LuxtronikInputRegister(6, ModbusDataType.U16, Feature = LuxtronikFeature.Cooling)]
    [State(IsDiscrete = true, Position = 5)]
    public partial LuxtronikModeStatus? CoolingStatus { get; internal set; }

    [LuxtronikInputRegister(7, ModbusDataType.U16, Feature = LuxtronikFeature.Pool)]
    [State(IsDiscrete = true, Position = 6)]
    public partial LuxtronikModeStatus? PoolHeatingStatus { get; internal set; }

    /// <summary>
    /// Gets the controller error number, 0 when there is no error.
    /// </summary>
    [LuxtronikInputRegister(201, ModbusDataType.U16)]
    [State(IsDiscrete = true, Position = 7)]
    public partial ushort? ErrorCode { get; internal set; }

    [LuxtronikInputRegister(202, ModbusDataType.U16)]
    [State(IsDiscrete = true, Position = 8)]
    public partial LuxtronikBufferType? BufferType { get; internal set; }

    [LuxtronikInputRegister(203, ModbusDataType.U16)]
    [State(Unit = StateUnit.Minute, Position = 9)]
    public partial int? MinimumOffTime { get; internal set; }

    [LuxtronikInputRegister(204, ModbusDataType.U16)]
    [State(Unit = StateUnit.Minute, Position = 10)]
    public partial int? MinimumRunTime { get; internal set; }

    [LuxtronikInputRegister(207, ModbusDataType.U16, Feature = LuxtronikFeature.Cooling)]
    [State(IsDiscrete = true, Position = 11)]
    public partial bool? CoolingReleased { get; internal set; }

    [Derived]
    [State(IsDiscrete = true, Position = 20)]
    public bool? IsCompressorRunning => HeatPumpStatus is { } status
        ? (status & (LuxtronikHeatPumpStatus.Compressor1 | LuxtronikHeatPumpStatus.Compressor2)) != 0
        : null;

    [Derived]
    [State(IsDiscrete = true, Position = 21)]
    public bool? IsAuxiliaryHeaterRunning => HeatPumpStatus is { } status
        ? (status & (LuxtronikHeatPumpStatus.AuxiliaryHeater1 | LuxtronikHeatPumpStatus.AuxiliaryHeater2 | LuxtronikHeatPumpStatus.AuxiliaryHeater3)) != 0
        : null;
}
