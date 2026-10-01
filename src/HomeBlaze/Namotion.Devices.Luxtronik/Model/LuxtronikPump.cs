// Register map: AIT SHI manual 83026900aDE; firmware gates: python-luxtronik 02afea84bd5bf3ee87445de6f2a42b8029983169.
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Devices;
using Namotion.Devices.Luxtronik.Attributes;
using Namotion.Devices.Luxtronik.Enums;
using Namotion.Devices.Luxtronik.Gating;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Modbus;

namespace Namotion.Devices.Luxtronik.Model;

/// <summary>
/// One pump output (inputs 10350 to 10356), firmware 3.92 and later. Its <see cref="BaseAddress"/> is the input register address itself.
/// </summary>
[InterceptorSubject]
public partial class LuxtronikPump : ISwitchState, ITitleProvider, IModbusBaseAddressProvider, ILuxtronikRequirements
{
    /// <summary>
    /// Initializes a pump titled <paramref name="title"/> for the input register at <paramref name="address"/>.
    /// </summary>
    public LuxtronikPump(int address, string title)
    {
        BaseAddress = address;
        Title = title;
        IsOn = null;
    }

    /// <inheritdoc />
    public int BaseAddress { get; }

    /// <inheritdoc />
    public string? Title { get; }

    /// <inheritdoc />
    [LuxtronikInputRegister(0, ModbusDataType.U16)]
    [State(IsDiscrete = true)]
    public partial bool? IsOn { get; internal set; }

    Version? ILuxtronikRequirements.MinimumFirmwareVersion => LuxtronikGating.Firmware392;

    LuxtronikFunction ILuxtronikRequirements.RequiredFunction => LuxtronikFunction.None;
}
