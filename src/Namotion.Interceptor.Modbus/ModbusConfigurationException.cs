namespace Namotion.Interceptor.Modbus;

/// <summary>
/// Thrown when a <see cref="Attributes.ModbusRegisterAttribute"/> mapping is invalid.
/// </summary>
public sealed class ModbusConfigurationException : Exception
{
    /// <summary>
    /// Initializes a new instance with a message describing the invalid mapping.
    /// </summary>
    public ModbusConfigurationException(string message)
        : base(message)
    {
    }

    internal static ModbusConfigurationException ForMapping(string propertyPath, string message)
        => new($"Invalid Modbus mapping on {propertyPath}: {message}");
}
