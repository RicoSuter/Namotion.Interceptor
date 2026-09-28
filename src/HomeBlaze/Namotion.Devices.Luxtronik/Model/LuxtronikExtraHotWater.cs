// Register map: AIT SHI manual 83026900aDE; firmware gates: python-luxtronik 02afea84bd5bf3ee87445de6f2a42b8029983169.
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Modbus;

namespace Namotion.Devices.Luxtronik;

/// <summary>
/// Extra hot water request state (inputs 10500 to 10502), firmware 3.92 and later.
/// </summary>
[InterceptorSubject]
public partial class LuxtronikExtraHotWater : IModbusBaseAddressProvider, ILuxtronikGatedSubject
{
    public LuxtronikExtraHotWater()
    {
        Setpoint = null;
        Duration = null;
        RemainingDuration = null;
    }

    public int BaseAddress => 10500;

    [LuxtronikInputRegister(0, ModbusDataType.S16, Scale = 0.1)]
    [State(Unit = StateUnit.DegreeCelsius, Position = 1)]
    public partial decimal? Setpoint { get; internal set; }

    [LuxtronikInputRegister(1, ModbusDataType.S16)]
    [State(Unit = StateUnit.Minute, Position = 2)]
    public partial int? Duration { get; internal set; }

    [LuxtronikInputRegister(2, ModbusDataType.S16)]
    [State(Unit = StateUnit.Minute, Position = 3)]
    public partial int? RemainingDuration { get; internal set; }

    Version? ILuxtronikGatedSubject.MinimumFirmwareVersion => LuxtronikGating.Firmware392;

    LuxtronikFeature ILuxtronikGatedSubject.Feature => LuxtronikFeature.None;
}
