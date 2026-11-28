namespace Namotion.ZWave.Transport;

/// <summary>
/// Opens the byte stream to a Z-Wave controller.
/// </summary>
public interface IZWaveTransport
{
    /// <summary>
    /// Opens a new stream to the controller. The caller owns the stream and disposes it.
    /// </summary>
    /// <remarks>
    /// <see cref="ZWaveController.ConnectAsync"/> reports every exception other than the canceled token as a
    /// <see cref="ZWaveConnectionException"/> with the exception as its inner exception, and names the transport by
    /// its <see cref="object.ToString"/>, so an implementation overrides it with what it connects to.
    /// </remarks>
    ValueTask<Stream> OpenAsync(CancellationToken cancellationToken);
}
