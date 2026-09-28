// Register map: AIT SHI manual 83026900aDE; firmware gates: python-luxtronik 02afea84bd5bf3ee87445de6f2a42b8029983169.
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Modbus;

namespace Namotion.Devices.Luxtronik;

/// <summary>
/// Operating mode locks (holding 10050 to 10053).
/// </summary>
[InterceptorSubject]
public partial class LuxtronikLocks : IModbusBaseAddressProvider
{
    public LuxtronikLocks()
    {
        Heating = null;
        HotWater = null;
        Cooling = null;
        Pool = null;
    }

    public int BaseAddress => 10050;

    [LuxtronikHoldingRegister(0, ModbusDataType.U16, MinimumFirmware = "3.92.0")]
    [State(IsDiscrete = true, Position = 1)]
    public partial bool? Heating { get; internal set; }

    [LuxtronikHoldingRegister(1, ModbusDataType.U16, MinimumFirmware = "3.92.0")]
    [State(IsDiscrete = true, Position = 2)]
    public partial bool? HotWater { get; internal set; }

    [LuxtronikHoldingRegister(2, ModbusDataType.U16, Feature = LuxtronikFeature.Cooling)]
    [State(IsDiscrete = true, Position = 3)]
    public partial bool? Cooling { get; internal set; }

    [LuxtronikHoldingRegister(3, ModbusDataType.U16, Feature = LuxtronikFeature.Pool)]
    [State(IsDiscrete = true, Position = 4)]
    public partial bool? Pool { get; internal set; }
}
