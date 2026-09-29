// Register map: AIT SHI manual 83026900aDE; firmware gates: python-luxtronik 02afea84bd5bf3ee87445de6f2a42b8029983169.
using HomeBlaze.Abstractions.Attributes;
using Namotion.Devices.Luxtronik.Attributes;
using Namotion.Devices.Luxtronik.Enums;
using Namotion.Devices.Luxtronik.Gating;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Modbus;

namespace Namotion.Devices.Luxtronik.Model;

/// <summary>
/// Circulation and extra hot water requests (holding 10070 and 10071), firmware 3.92 and later.
/// </summary>
[InterceptorSubject]
public partial class LuxtronikHotWaterRequests : IModbusBaseAddressProvider, ILuxtronikGatedSubject
{
    /// <summary>
    /// Initializes the registers as unknown (<c>null</c>) until they are read.
    /// </summary>
    public LuxtronikHotWaterRequests()
    {
        Circulation = null;
        ExtraHotWater = null;
    }

    /// <inheritdoc />
    public int BaseAddress => 10070;

    /// <summary>
    /// Gets whether hot water circulation is requested.
    /// </summary>
    [LuxtronikHoldingRegister(0, ModbusDataType.U16)]
    [State(IsDiscrete = true, Position = 1)]
    public partial bool? Circulation { get; internal set; }

    /// <summary>
    /// Gets whether extra hot water is requested.
    /// </summary>
    [LuxtronikHoldingRegister(1, ModbusDataType.U16)]
    [State(IsDiscrete = true, Position = 2)]
    public partial bool? ExtraHotWater { get; internal set; }

    Version? ILuxtronikGatedSubject.MinimumFirmwareVersion => LuxtronikGating.Firmware392;

    LuxtronikFeature ILuxtronikGatedSubject.Feature => LuxtronikFeature.None;
}
