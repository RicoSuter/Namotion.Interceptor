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
    private readonly TimeSpan _writeTimeout;
    private readonly TimeProvider _timeProvider;
    private readonly FrameParser _parser = new();
    private readonly byte[] _buffer = new byte[256];
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public FrameStream(Stream stream, TimeSpan writeTimeout, TimeProvider timeProvider, ILogger logger)
    {
        _stream = stream;
        _writeTimeout = writeTimeout;
        _timeProvider = timeProvider;
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
    /// Writes and flushes a frame, within the write timeout.
    /// </summary>
    /// <exception cref="ZWaveConnectionException">
    /// The stream failed or the write timed out; any failure other than the canceled token is reported this way.
    /// </exception>
    public async Task WriteAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_logger.IsEnabled(LogLevel.Trace))
            {
                _logger.LogTrace("Sending {Bytes}.", Convert.ToHexString(frame.Span));
            }

            // A stalled network bridge or a hung USB device never completes a write, which would block the driver for good.
            var write = WriteAndFlushAsync(frame, cancellationToken);
            await (write.IsCompleted ? write : write.WaitAsync(_writeTimeout, _timeProvider, cancellationToken)).ConfigureAwait(false);
        }
        catch (TimeoutException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ZWaveConnectionException("Writing to the Z-Wave controller did not complete in time.", exception);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ZWaveConnectionException("Writing to the Z-Wave controller failed.", exception);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void Dispose() => _writeLock.Dispose();

    private async Task WriteAndFlushAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken)
    {
        await _stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
