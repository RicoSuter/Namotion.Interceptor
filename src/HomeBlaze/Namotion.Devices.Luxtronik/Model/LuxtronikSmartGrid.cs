// Register map: AIT SHI manual 83026900aDE; firmware gates: python-luxtronik 02afea84bd5bf3ee87445de6f2a42b8029983169.
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Modbus;

namespace Namotion.Devices.Luxtronik;

/// <summary>
/// Smart Grid signals from the utility (inputs 10360 and 10361), firmware 3.92 and later.
/// </summary>
[InterceptorSubject]
public partial class LuxtronikSmartGrid : IModbusBaseAddressProvider, ILuxtronikGatedSubject
{
    public LuxtronikSmartGrid()
    {
        Evu1 = null;
        Evu2 = null;
    }

    public int BaseAddress => 10360;

    [LuxtronikInputRegister(0, ModbusDataType.U16)]
    [State(IsDiscrete = true, Position = 1)]
    public partial bool? Evu1 { get; internal set; }

    [LuxtronikInputRegister(1, ModbusDataType.U16)]
    [State(IsDiscrete = true, Position = 2)]
    public partial bool? Evu2 { get; internal set; }

    [Derived]
    [State(IsDiscrete = true, Position = 3)]
    public LuxtronikSmartGridState? State => (Evu1, Evu2) switch
    {
        (true, false) => LuxtronikSmartGridState.Locked,
        (false, false) => LuxtronikSmartGridState.Reduced,
        (false, true) => LuxtronikSmartGridState.Normal,
        (true, true) => LuxtronikSmartGridState.Increased,
        _ => null
    };

    string? ILuxtronikGatedSubject.MinimumFirmware => "3.92.0";

    LuxtronikFeature ILuxtronikGatedSubject.Feature => LuxtronikFeature.None;
}
