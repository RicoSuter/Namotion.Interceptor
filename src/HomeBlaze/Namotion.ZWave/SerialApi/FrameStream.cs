using Microsoft.Extensions.Logging;

namespace Namotion.ZWave.SerialApi;

/// <summary>
/// Reads frames from and writes frames to the controller's byte stream. One reader at a time; writes are serialized.
/// Does not own the stream.
/// </summary>
internal sealed class FrameStream : IDisposable
{
    private readonly Stream _stream;
    private readonly ILogger _logger;
    private readonly FrameParser _parser = new();
    private readonly byte[] _buffer = new byte[256];
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public FrameStream(Stream stream, ILogger logger)
    {
        _stream = stream;
        _logger = logger;
    }

    public static ReadOnlyMemory<byte> Ack { get; } = new[] { FrameHeader.Ack };

    public static ReadOnlyMemory<byte> Nak { get; } = new[] { FrameHeader.Nak };

    /// <summary>
    /// Reads the next frame, or returns <c>null</c> at the end of the stream. Not thread-safe.
    /// </summary>
    public async ValueTask<ParsedFrame?> ReadAsync(CancellationToken cancellationToken)
    {
        ParsedFrame frame;
        while (!_parser.TryRead(out frame))
        {
            var count = await _stream.ReadAsync(_buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                return null;
            }

            if (_logger.IsEnabled(LogLevel.Trace))
            {
                _logger.LogTrace("Received {Bytes}.", Convert.ToHexString(_buffer, 0, count));
            }

            _parser.Append(_buffer.AsSpan(0, count));
        }

        return frame;
    }

    /// <summary>
    /// Writes and flushes a frame.
    /// </summary>
    /// <exception cref="ZWaveConnectionException">The stream failed.</exception>
    public async Task WriteAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_logger.IsEnabled(LogLevel.Trace))
            {
                _logger.LogTrace("Sending {Bytes}.", Convert.ToHexString(frame.Span));
            }

            await _stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException && !cancellationToken.IsCancellationRequested)
        {
            throw new ZWaveConnectionException("Writing to the Z-Wave controller failed.", exception);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void Dispose() => _writeLock.Dispose();
}
