using Namotion.Interceptor.Modbus;

namespace Namotion.Devices.Luxtronik.Attributes;

/// <summary>
/// A read-only Smart Home Interface input register.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class LuxtronikInputRegisterAttribute : LuxtronikRegisterAttribute
{
    /// <summary>
    /// Initializes an input register at <paramref name="address"/>, relative to the subject's base address.
    /// </summary>
    public LuxtronikInputRegisterAttribute(int address, ModbusDataType dataType)
        : base(address, dataType)
    {
        Space = ModbusAddressSpace.InputRegister;
    }
}
