namespace Namotion.ZWave;

/// <summary>
/// The controller rejected a request, for example with a NAK or a CAN after the last attempt.
/// </summary>
public sealed class ZWaveControllerException : ZWaveException
{
    public ZWaveControllerException()
    {
    }

    public ZWaveControllerException(string message)
        : base(message)
    {
    }

    public ZWaveControllerException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
