// Register map: AIT SHI manual 83026900aDE.
using HomeBlaze.Abstractions.Attributes;
using Namotion.Devices.Luxtronik.Attributes;
using Namotion.Devices.Luxtronik.Enums;
using Namotion.Devices.Luxtronik.Gating;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Modbus;

namespace Namotion.Devices.Luxtronik.Model;

/// <summary>
/// The cooling setpoint configuration of a mixing circuit sent over the SHI: mode, setpoint and offset.
/// </summary>
[InterceptorSubject]
public partial class LuxtronikCoolingSmartHomeControl : IModbusBaseAddressProvider, ILuxtronikGate
{
    private readonly LuxtronikFunction _function;

    /// <summary>
    /// Initializes a cooling control block at <paramref name="baseAddress"/> whose registers require <paramref name="function"/>.
    /// </summary>
    public LuxtronikCoolingSmartHomeControl(int baseAddress, LuxtronikFunction function)
    {
        BaseAddress = baseAddress;
        _function = function;
        Mode = null;
        Setpoint = null;
        Offset = null;
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

    Version? ILuxtronikGate.MinimumFirmwareVersion => null;

    LuxtronikFunction ILuxtronikGate.Function => _function;
}
