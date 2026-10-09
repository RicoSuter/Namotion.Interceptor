// Register map: AIT SHI manual 83026900aDE; firmware gates: python-luxtronik 02afea84bd5bf3ee87445de6f2a42b8029983169.
using HomeBlaze.Abstractions.Attributes;
using Namotion.Devices.Luxtronik.Attributes;
using Namotion.Devices.Luxtronik.Enums;
using Namotion.Devices.Luxtronik.Gating;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Modbus;

namespace Namotion.Devices.Luxtronik.Model;

/// <summary>
/// Smart Grid signals from the utility (inputs 10360 and 10361), firmware 3.92 and later.
/// </summary>
[InterceptorSubject]
public partial class LuxtronikSmartGrid : IModbusBaseAddressProvider, ILuxtronikRequirements
{
    /// <summary>
    /// Initializes the registers as unknown (<c>null</c>) until they are read.
    /// </summary>
    public LuxtronikSmartGrid()
    {
        IsEvu1Active = null;
        IsEvu2Active = null;
    }

    /// <inheritdoc />
    public int BaseAddress => 10360;

    /// <summary>
    /// Gets whether the EVU1 input (SG 1) is on. With Smart Grid switched off in the controller this is the
    /// utility lock contact, which is on while the heat pump is released and off during a lock time.
    /// </summary>
    [LuxtronikInputRegister(0, ModbusDataType.U16)]
    [State(IsDiscrete = true, Position = 1)]
    public partial bool? IsEvu1Active { get; internal set; }

    /// <summary>
    /// Gets whether the EVU2 input (SG 2) is on.
    /// </summary>
    [LuxtronikInputRegister(1, ModbusDataType.U16)]
    [State(IsDiscrete = true, Position = 2)]
    public partial bool? IsEvu2Active { get; internal set; }

    /// <summary>
    /// Gets the Smart Grid state from both EVU signals, or <c>null</c> when either is unknown.
    /// </summary>
    /// <remarks>
    /// Only meaningful while Smart Grid is switched on in the controller, which the SHI does not report.
    /// </remarks>
    [Derived]
    [State(IsDiscrete = true, Position = 3)]
    public LuxtronikSmartGridState? State => (IsEvu1Active, IsEvu2Active) switch
    {
        (true, false) => LuxtronikSmartGridState.Locked,
        (false, false) => LuxtronikSmartGridState.Reduced,
        (false, true) => LuxtronikSmartGridState.Normal,
        (true, true) => LuxtronikSmartGridState.Increased,
        _ => null
    };

    Version? ILuxtronikRequirements.MinimumFirmwareVersion => LuxtronikGating.Firmware392;

    LuxtronikFunction ILuxtronikRequirements.RequiredFunction => LuxtronikFunction.None;
}
