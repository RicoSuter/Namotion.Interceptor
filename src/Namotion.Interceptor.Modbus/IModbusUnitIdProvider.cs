namespace Namotion.Interceptor.Modbus;

/// <summary>
/// Supplies the Modbus unit ID for the registers of this subject and its children.
/// </summary>
/// <remarks>Read when the connector builds its read plan on connect.</remarks>
public interface IModbusUnitIdProvider
{
    /// <summary>
    /// Gets the unit ID the registers of this subject and its children are read from.
    /// </summary>
    byte UnitId { get; }
}
