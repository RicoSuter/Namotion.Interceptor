// Register map: AIT SHI manual 83026900aDE; firmware gates: python-luxtronik 02afea84bd5bf3ee87445de6f2a42b8029983169.
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Modbus;

namespace Namotion.Devices.Luxtronik;

/// <summary>
/// Operating hours since installation (inputs 10404 to 10417), firmware 3.92 and later.
/// </summary>
[InterceptorSubject]
public partial class LuxtronikRuntime : IModbusBaseAddressProvider, ILuxtronikGatedSubject
{
    public LuxtronikRuntime()
    {
        HeatPump = null;
        Heating = null;
        HotWater = null;
        Cooling = null;
        Pool = null;
        Solar = null;
    }

    public int BaseAddress => 10404;

    [LuxtronikInputRegister(0, ModbusDataType.U32)]
    [State(Unit = StateUnit.Hour, IsCumulative = true, Position = 1)]
    public partial decimal? HeatPump { get; internal set; }

    [LuxtronikInputRegister(2, ModbusDataType.U32, Feature = LuxtronikFeature.Heating)]
    [State(Unit = StateUnit.Hour, IsCumulative = true, Position = 2)]
    public partial decimal? Heating { get; internal set; }

    [LuxtronikInputRegister(4, ModbusDataType.U32, Feature = LuxtronikFeature.HotWater)]
    [State(Unit = StateUnit.Hour, IsCumulative = true, Position = 3)]
    public partial decimal? HotWater { get; internal set; }

    [LuxtronikInputRegister(6, ModbusDataType.U32, Feature = LuxtronikFeature.Cooling)]
    [State(Unit = StateUnit.Hour, IsCumulative = true, Position = 4)]
    public partial decimal? Cooling { get; internal set; }

    [LuxtronikInputRegister(8, ModbusDataType.U32, Feature = LuxtronikFeature.Pool)]
    [State(Unit = StateUnit.Hour, IsCumulative = true, Position = 5)]
    public partial decimal? Pool { get; internal set; }

    [LuxtronikInputRegister(12, ModbusDataType.U32, Feature = LuxtronikFeature.Solar)]
    [State(Unit = StateUnit.Hour, IsCumulative = true, Position = 6)]
    public partial decimal? Solar { get; internal set; }

    string? ILuxtronikGatedSubject.MinimumFirmware => "3.92.0";

    LuxtronikFeature ILuxtronikGatedSubject.Feature => LuxtronikFeature.None;
}
