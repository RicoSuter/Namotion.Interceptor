namespace Namotion.Interceptor.Modbus;

/// <summary>
/// Supplies the address the register addresses of this subject are relative to. Not inherited by child subjects.
/// </summary>
/// <remarks>Read when the connector builds its read plan on connect.</remarks>
public interface IModbusBaseAddressProvider
{
    /// <summary>
    /// Gets the address added to the <see cref="Attributes.ModbusRegisterAttribute.Address"/> of this subject's mappings.
    /// </summary>
    int BaseAddress { get; }
}
