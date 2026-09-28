using Namotion.Interceptor.Modbus;
using Namotion.Interceptor.Modbus.Attributes;

namespace Namotion.Devices.Luxtronik;

/// <summary>
/// A read-only Smart Home Interface input register; 0x7FFF and 0x7FFFFFFF map to <c>null</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class LuxtronikInputRegisterAttribute : ModbusRegisterAttribute, ILuxtronikRegisterGate
{
    private Version? _minimumFirmwareVersion;

    public LuxtronikInputRegisterAttribute(int address, ModbusDataType dataType)
        : base(address, dataType)
    {
        Space = ModbusAddressSpace.InputRegister;
        NotAvailableValue = ModbusNotAvailableValue.SignedMaximum;
    }

    /// <summary>
    /// Gets the first firmware version providing the register, such as "3.92.0".
    /// </summary>
    public string? MinimumFirmware { get; init; }

    /// <summary>
    /// Gets the controller function that must be configured for the register to be read;
    /// <see cref="LuxtronikFeature.None"/> (the default) means no requirement.
    /// </summary>
    public LuxtronikFeature Feature { get; init; } = LuxtronikFeature.None;

    Version? ILuxtronikRegisterGate.MinimumFirmwareVersion =>
        MinimumFirmware is null ? null : _minimumFirmwareVersion ??= Version.Parse(MinimumFirmware);
}
