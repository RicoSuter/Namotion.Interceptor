// Register map: AIT SHI manual 83026900aDE; firmware gates: python-luxtronik 02afea84bd5bf3ee87445de6f2a42b8029983169.
using HomeBlaze.Abstractions.Attributes;
using Namotion.Devices.Luxtronik.Attributes;
using Namotion.Devices.Luxtronik.Enums;
using Namotion.Devices.Luxtronik.Gating;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Modbus;

namespace Namotion.Devices.Luxtronik.Model;

/// <summary>
/// Extra hot water: the SHI request (holding 10071) and its state (inputs 10500 to 10502), firmware 3.92 and later.
/// </summary>
[InterceptorSubject]
public partial class LuxtronikExtraHotWater : ILuxtronikGatedSubject
{
    /// <summary>
    /// Initializes the registers as unknown (<c>null</c>) until they are read.
    /// </summary>
    public LuxtronikExtraHotWater()
    {
        Requested = null;
        Target = null;
        Duration = null;
        RemainingDuration = null;
    }

    /// <summary>
    /// Gets whether extra hot water is requested over the SHI.
    /// </summary>
    [LuxtronikHoldingRegister(10071, ModbusDataType.U16)]
    [State(IsDiscrete = true, Position = 1)]
    public partial bool? Requested { get; internal set; }

    /// <summary>
    /// Gets the extra hot water target temperature.
    /// </summary>
    [LuxtronikInputRegister(10500, ModbusDataType.S16, Scale = 0.1)]
    [State(Unit = StateUnit.DegreeCelsius, Position = 2)]
    public partial decimal? Target { get; internal set; }

    /// <summary>
    /// Gets the requested extra hot water duration.
    /// </summary>
    [LuxtronikInputRegister(10501, ModbusDataType.S16)]
    [State(Unit = StateUnit.Minute, Position = 3)]
    public partial int? Duration { get; internal set; }

    /// <summary>
    /// Gets the remaining extra hot water duration.
    /// </summary>
    [LuxtronikInputRegister(10502, ModbusDataType.S16)]
    [State(Unit = StateUnit.Minute, Position = 4)]
    public partial int? RemainingDuration { get; internal set; }

    Version? ILuxtronikGatedSubject.MinimumFirmwareVersion => LuxtronikGating.Firmware392;

    LuxtronikFunction ILuxtronikGatedSubject.Function => LuxtronikFunction.None;
}
