// Register map: AIT SHI manual 83026900aDE.
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Modbus;

namespace Namotion.Devices.Luxtronik;

/// <summary>
/// Power consumption limitation (holding 10040 and 10041) in watts. The controller reports kW in tenths.
/// </summary>
[InterceptorSubject]
public partial class LuxtronikPowerLimit : IModbusBaseAddressProvider
{
    public LuxtronikPowerLimit()
    {
        Mode = null;
        Limit = null;
    }

    public int BaseAddress => 10040;

    [LuxtronikHoldingRegister(0, ModbusDataType.U16)]
    [State(IsDiscrete = true, Position = 1)]
    public partial LuxtronikPowerLimitMode? Mode { get; internal set; }

    [LuxtronikHoldingRegister(1, ModbusDataType.U16, Scale = 100)]
    [State(Unit = StateUnit.Watt, Position = 2)]
    public partial decimal? Limit { get; internal set; }
}
