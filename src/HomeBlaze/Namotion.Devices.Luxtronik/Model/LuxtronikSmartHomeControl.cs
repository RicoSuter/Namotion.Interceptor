// Register map: AIT SHI manual 83026900aDE; firmware gates: python-luxtronik 02afea84bd5bf3ee87445de6f2a42b8029983169.
using HomeBlaze.Abstractions.Attributes;
using Namotion.Devices.Luxtronik.Attributes;
using Namotion.Devices.Luxtronik.Enums;
using Namotion.Devices.Luxtronik.Gating;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Modbus;

namespace Namotion.Devices.Luxtronik.Model;

/// <summary>
/// The setpoint configuration a smart home system sends over the SHI (manual: Sollwertkonfiguration; shown on the controller under Empfangene Daten): mode, setpoint, offset and level, reused at several holding addresses.
/// </summary>
[InterceptorSubject]
public partial class LuxtronikSmartHomeControl : IModbusBaseAddressProvider, ILuxtronikRequirements
{
    private readonly LuxtronikFunction _function;

    /// <summary>
    /// Initializes a control block at <paramref name="baseAddress"/> whose registers require <paramref name="function"/>.
    /// </summary>
    public LuxtronikSmartHomeControl(int baseAddress, LuxtronikFunction function)
    {
        BaseAddress = baseAddress;
        _function = function;
        Mode = null;
        Setpoint = null;
        Offset = null;
        Level = null;
    }

    /// <inheritdoc />
    public int BaseAddress { get; }

    /// <summary>
    /// Gets how the control influences its circuit.
    /// </summary>
    [LuxtronikHoldingRegister(0, ModbusDataType.U16)]
    [State(IsDiscrete = true, Position = 1)]
    public partial LuxtronikControlMode? Mode { get; internal set; }

    /// <summary>
    /// Gets the setpoint, which only applies when <see cref="Mode"/> is <see cref="LuxtronikControlMode.Setpoint"/>.
    /// </summary>
    [LuxtronikHoldingRegister(1, ModbusDataType.U16, Scale = 0.1)]
    [State(Unit = StateUnit.DegreeCelsius, Position = 2)]
    public partial decimal? Setpoint { get; internal set; }

    /// <summary>
    /// Gets the offset, which only applies when <see cref="Mode"/> is <see cref="LuxtronikControlMode.Offset"/>.
    /// </summary>
    [LuxtronikHoldingRegister(2, ModbusDataType.S16, Scale = 0.1)]
    [State(Unit = StateUnit.Kelvin, Position = 3)]
    public partial decimal? Offset { get; internal set; }

    /// <summary>
    /// Gets the level, which only applies when <see cref="Mode"/> is <see cref="LuxtronikControlMode.Level"/>.
    /// </summary>
    [LuxtronikHoldingRegister(3, ModbusDataType.U16, MinimumFirmware = "3.92.0")]
    [State(IsDiscrete = true, Position = 4)]
    public partial LuxtronikLevelMode? Level { get; internal set; }

    Version? ILuxtronikRequirements.MinimumFirmwareVersion => null;

    LuxtronikFunction ILuxtronikRequirements.RequiredFunction => _function;
}
