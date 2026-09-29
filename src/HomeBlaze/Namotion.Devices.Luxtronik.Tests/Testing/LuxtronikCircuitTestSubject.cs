using Namotion.Devices.Luxtronik.Attributes;
using Namotion.Devices.Luxtronik.Enums;
using Namotion.Devices.Luxtronik.Gating;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Modbus;

namespace Namotion.Devices.Luxtronik.Tests.Testing;

[InterceptorSubject]
public partial class LuxtronikCircuitTestSubject : ILuxtronikCircuitSubject
{
    public LuxtronikCircuitTestSubject()
    {
        CircuitGated = null;
        Ungated = null;
    }

    public int FeatureOffset { get; init; }

    [LuxtronikInputRegister(0, ModbusDataType.U16, Feature = LuxtronikFeature.MixingCircuit1Heating)]
    public partial ushort? CircuitGated { get; set; }

    [LuxtronikInputRegister(1, ModbusDataType.U16)]
    public partial ushort? Ungated { get; set; }
}
