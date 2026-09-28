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

    /// <summary>
    /// Gets whether the device does not support the request: 1 (illegal function), 2 (illegal data address) or
    /// 3 (illegal data value). Any other code, such as 6 (server busy) or a gateway error, may pass on a later try.
    /// </summary>
    internal bool IsPermanentRejection => ExceptionCode is 1 or 2 or 3;
}
