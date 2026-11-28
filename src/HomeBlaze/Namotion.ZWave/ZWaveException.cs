namespace Namotion.ZWave;

/// <summary>
/// The base of the errors that the Z-Wave SDK reports.
/// </summary>
public class ZWaveException : Exception
{
    public ZWaveException()
    {
    }

    public ZWaveException(string message)
        : base(message)
    {
    }

    public ZWaveException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
