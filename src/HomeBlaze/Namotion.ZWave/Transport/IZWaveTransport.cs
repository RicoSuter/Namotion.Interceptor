namespace Namotion.ZWave.Transport;

/// <summary>
/// Opens the byte stream to a Z-Wave controller.
/// </summary>
public interface IZWaveTransport
{
    /// <summary>
    /// Opens a new stream to the controller. The caller owns the stream and disposes it.
    /// </summary>
    ValueTask<Stream> OpenAsync(CancellationToken cancellationToken);
}
