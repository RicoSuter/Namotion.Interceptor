// Register map: AIT SHI manual 83026900aDE; firmware gates: python-luxtronik 02afea84bd5bf3ee87445de6f2a42b8029983169.
using HomeBlaze.Abstractions.Attributes;
using Namotion.Devices.Luxtronik.Attributes;
using Namotion.Devices.Luxtronik.Enums;
using Namotion.Devices.Luxtronik.Gating;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Modbus;

namespace Namotion.Devices.Luxtronik.Model;

/// <summary>
/// The overall setpoint configuration (holding 10065 to 10067), firmware 3.92 and later: an offset or level for the heating circuit and all mixing circuits. The individual configurations apply only in mode Individual.
/// </summary>
[InterceptorSubject]
public partial class LuxtronikOverallSmartHomeControl : IModbusBaseAddressProvider, ILuxtronikGatedSubject
{
    /// <summary>
    /// Initializes the registers as unknown (<c>null</c>) until they are read.
    /// </summary>
    public LuxtronikOverallSmartHomeControl()
    {
        Mode = null;
        Offset = null;
        Level = null;
    }

    /// <inheritdoc />
    public int BaseAddress => 10065;

    /// <summary>
    /// Gets how all heating circuits are controlled.
    /// </summary>
    [LuxtronikHoldingRegister(0, ModbusDataType.U16)]
    [State(IsDiscrete = true, Position = 1)]
    public partial LuxtronikOverallControlMode? Mode { get; internal set; }

    /// <summary>
    /// Gets the offset, which only applies when <see cref="Mode"/> is <see cref="LuxtronikOverallControlMode.Offset"/>.
    /// </summary>
    [LuxtronikHoldingRegister(1, ModbusDataType.S16, Scale = 0.1)]
    [State(Unit = StateUnit.Kelvin, Position = 2)]
    public partial decimal? Offset { get; internal set; }

    /// <summary>
    /// Gets the level, which only applies when <see cref="Mode"/> is <see cref="LuxtronikOverallControlMode.Level"/>.
    /// </summary>
    [LuxtronikHoldingRegister(2, ModbusDataType.U16)]
    [State(IsDiscrete = true, Position = 3)]
    public partial LuxtronikLevelMode? Level { get; internal set; }

    Version? ILuxtronikGatedSubject.MinimumFirmwareVersion => LuxtronikGating.Firmware392;

    LuxtronikFunction ILuxtronikGatedSubject.Function => LuxtronikFunction.None;
}
