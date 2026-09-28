namespace Namotion.Interceptor.Modbus;

/// <summary>
/// Thrown when a <see cref="Attributes.ModbusRegisterAttribute"/> mapping is invalid.
/// </summary>
public sealed class ModbusConfigurationException : Exception
{
    public ModbusConfigurationException(string message)
        : base(message)
    {
    }
}
