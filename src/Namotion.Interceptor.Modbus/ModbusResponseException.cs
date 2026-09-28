namespace Namotion.Interceptor.Modbus;

/// <summary>
/// Thrown when a device answers a request with a Modbus exception response, for example
/// 2 (illegal data address) for an unmapped register. The connection stays usable.
/// </summary>
public sealed class ModbusResponseException : Exception
{
    internal ModbusResponseException(int exceptionCode, string message, Exception innerException)
        : base(message, innerException)
    {
        ExceptionCode = exceptionCode;
    }

    /// <summary>
    /// Gets the Modbus exception code.
    /// </summary>
    public int ExceptionCode { get; }
}
