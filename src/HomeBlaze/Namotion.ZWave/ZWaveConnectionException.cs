namespace Namotion.ZWave;

/// <summary>
/// The connection to the controller failed or was closed. The controller instance cannot be used anymore.
/// </summary>
public sealed class ZWaveConnectionException : ZWaveException
{
    public ZWaveConnectionException()
    {
    }

    public ZWaveConnectionException(string message)
        : base(message)
    {
    }

    public ZWaveConnectionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
