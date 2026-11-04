namespace Namotion.Interceptor.Modbus;

/// <summary>
/// Supplies the scale factor properties of this subject's mappings when they live on another subject, such as a
/// repeated block sharing the scale factors of the block that contains it.
/// </summary>
/// <remarks>Read when the connector builds its read plan on connect.</remarks>
public interface IModbusScaleFactorProvider
{
    /// <summary>
    /// Gets the <see cref="ModbusDataType.S16"/> register property holding the power-of-ten exponent of the mapping of
    /// <paramref name="propertyName"/>, or <c>null</c> when this provider supplies no scale factor for it.
    /// </summary>
    PropertyReference? TryGetScaleFactorProperty(string propertyName);
}
