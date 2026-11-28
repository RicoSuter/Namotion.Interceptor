namespace Namotion.ZWave;

/// <summary>
/// The controller did not acknowledge or answer a request in time.
/// </summary>
public sealed class ZWaveTimeoutException : ZWaveException
{
    public ZWaveTimeoutException()
    {
    }

    public ZWaveTimeoutException(string message)
        : base(message)
    {
    }

    public ZWaveTimeoutException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
