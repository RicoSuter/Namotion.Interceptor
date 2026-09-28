// Register map: AIT SHI manual 83026900aDE; firmware gates: python-luxtronik 02afea84bd5bf3ee87445de6f2a42b8029983169.
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Modbus;

namespace Namotion.Devices.Luxtronik;

/// <summary>
/// Global heating control for all heating circuits (holding 10065 to 10067), firmware 3.92 and later.
/// </summary>
[InterceptorSubject]
public partial class LuxtronikOverallHeating : IModbusBaseAddressProvider, ILuxtronikGatedSubject
{
    public LuxtronikOverallHeating()
    {
        Mode = null;
        Offset = null;
        Level = null;
    }

    public int BaseAddress => 10065;

    [LuxtronikHoldingRegister(0, ModbusDataType.U16)]
    [State(IsDiscrete = true, Position = 1)]
    public partial LuxtronikOverallHeatingMode? Mode { get; internal set; }

    /// <summary>
    /// Gets the offset, which only applies when <see cref="Mode"/> is <see cref="LuxtronikOverallHeatingMode.Offset"/>.
    /// </summary>
    [LuxtronikHoldingRegister(1, ModbusDataType.S16, Scale = 0.1)]
    [State(Unit = StateUnit.Kelvin, Position = 2)]
    public partial decimal? Offset { get; internal set; }

    /// <summary>
    /// Gets the level, which only applies when <see cref="Mode"/> is <see cref="LuxtronikOverallHeatingMode.Level"/>.
    /// </summary>
    [LuxtronikHoldingRegister(2, ModbusDataType.U16)]
    [State(IsDiscrete = true, Position = 3)]
    public partial LuxtronikLevelMode? Level { get; internal set; }

    Version? ILuxtronikGatedSubject.MinimumFirmwareVersion => LuxtronikGating.Firmware392;

    LuxtronikFeature ILuxtronikGatedSubject.Feature => LuxtronikFeature.None;
}
