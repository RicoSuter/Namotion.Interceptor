using Namotion.Interceptor.Modbus;

namespace Namotion.Devices.Luxtronik.Attributes;

/// <summary>
/// A Smart Home Interface holding register, read only in this version.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class LuxtronikHoldingRegisterAttribute : LuxtronikRegisterAttribute
{
    /// <summary>
    /// Initializes a holding register at <paramref name="address"/>, relative to the subject's base address.
    /// </summary>
    public LuxtronikHoldingRegisterAttribute(int address, ModbusDataType dataType)
        : base(address, dataType)
    {
        Space = ModbusAddressSpace.HoldingRegister;
        Access = ModbusAccess.ReadOnly;
    }
}
