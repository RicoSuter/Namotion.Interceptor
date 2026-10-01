using Namotion.Devices.Luxtronik.Enums;
using Namotion.Devices.Luxtronik.Gating;
using Namotion.Interceptor.Modbus;
using Namotion.Interceptor.Modbus.Attributes;

namespace Namotion.Devices.Luxtronik.Attributes;

/// <summary>
/// A Smart Home Interface register with its firmware and function requirements; 0x7FFF and 0x7FFFFFFF map to <c>null</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public abstract class LuxtronikRegisterAttribute : ModbusRegisterAttribute, ILuxtronikRequirements
{
    private Version? _minimumFirmwareVersion;

    /// <summary>
    /// Initializes a register at <paramref name="address"/>, relative to the subject's base address.
    /// </summary>
    protected LuxtronikRegisterAttribute(int address, ModbusDataType dataType)
        : base(address, dataType)
    {
        NotAvailableValue = ModbusNotAvailableValue.SignedMaximum;
    }

    /// <summary>
    /// Gets the first firmware version providing the register, such as "3.92.0".
    /// </summary>
    public string? MinimumFirmware { get; init; }

    /// <summary>
    /// Gets the controller function that must be configured for the register to be read;
    /// <see cref="LuxtronikFunction.None"/> (the default) means no requirement.
    /// </summary>
    public LuxtronikFunction RequiredFunction { get; init; } = LuxtronikFunction.None;

    Version? ILuxtronikRequirements.MinimumFirmwareVersion =>
        MinimumFirmware is null ? null : _minimumFirmwareVersion ??= Version.Parse(MinimumFirmware);
}
