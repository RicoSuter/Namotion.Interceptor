// Register map: AIT SHI manual 83026900aDE; firmware gates: python-luxtronik 02afea84bd5bf3ee87445de6f2a42b8029983169.
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Modbus;

namespace Namotion.Devices.Luxtronik;

/// <summary>
/// Pump outputs (inputs 10350 to 10356), firmware 3.92 and later.
/// </summary>
[InterceptorSubject]
public partial class LuxtronikOutputs : IModbusBaseAddressProvider, ILuxtronikGatedSubject
{
    public LuxtronikOutputs()
    {
        BrineCirculationPump = null;
        MixingCircuit1Pump = null;
        MixingCircuit2Pump = null;
        MixingCircuit3Pump = null;
        HeatingCirculationPump = null;
        HotWaterCirculationPump = null;
        CirculationPump = null;
    }

    public int BaseAddress => 10350;

    [LuxtronikInputRegister(0, ModbusDataType.U16)]
    [State(IsDiscrete = true, Position = 1)]
    public partial bool? BrineCirculationPump { get; internal set; }

    [LuxtronikInputRegister(1, ModbusDataType.U16)]
    [State(IsDiscrete = true, Position = 2)]
    public partial bool? MixingCircuit1Pump { get; internal set; }

    [LuxtronikInputRegister(2, ModbusDataType.U16)]
    [State(IsDiscrete = true, Position = 3)]
    public partial bool? MixingCircuit2Pump { get; internal set; }

    [LuxtronikInputRegister(3, ModbusDataType.U16)]
    [State(IsDiscrete = true, Position = 4)]
    public partial bool? MixingCircuit3Pump { get; internal set; }

    [LuxtronikInputRegister(4, ModbusDataType.U16)]
    [State(IsDiscrete = true, Position = 5)]
    public partial bool? HeatingCirculationPump { get; internal set; }

    [LuxtronikInputRegister(5, ModbusDataType.U16)]
    [State(IsDiscrete = true, Position = 6)]
    public partial bool? HotWaterCirculationPump { get; internal set; }

    [LuxtronikInputRegister(6, ModbusDataType.U16)]
    [State(IsDiscrete = true, Position = 7)]
    public partial bool? CirculationPump { get; internal set; }

    Version? ILuxtronikGatedSubject.MinimumFirmwareVersion => LuxtronikGating.Firmware392;

    LuxtronikFeature ILuxtronikGatedSubject.Feature => LuxtronikFeature.None;
}
