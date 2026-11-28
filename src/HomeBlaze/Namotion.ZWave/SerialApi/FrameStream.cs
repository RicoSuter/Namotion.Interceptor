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
    private readonly TimeSpan _frameDeliveryTimeout;
    private readonly TimeProvider _timeProvider;
    private readonly FrameParser _parser = new();
    private readonly byte[] _buffer = new byte[256];
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    // A read that outlived a frame delivery timeout; the next read continues with it. Used by the reader only.
    private Task<int>? _pendingRead;
    private long _partialFramePosition = -1;
    private long _partialFrameStartedAt;

    public FrameStream(Stream stream, ZWaveControllerOptions options, ILogger logger)
    {
        _stream = stream;
        _writeTimeout = options.AcknowledgementTimeout;
        _frameDeliveryTimeout = options.FrameDeliveryTimeout;
        _timeProvider = options.TimeProvider;
        _logger = logger;
    }

    public static ReadOnlyMemory<byte> Ack { get; } = new[] { FrameHeader.Ack };

    public static ReadOnlyMemory<byte> Nak { get; } = new[] { FrameHeader.Nak };

    /// <summary>
    /// Reads the next frame, or returns <c>null</c> at the end of the stream. Not thread-safe.
    /// </summary>
    /// <remarks>
    /// A frame that does not complete within the frame delivery timeout after its start of frame byte is dropped,
    /// and the bytes after that byte are parsed again.
    /// </remarks>
    public async ValueTask<ParsedFrame?> ReadAsync(CancellationToken cancellationToken)
    {
        ParsedFrame frame;
        while (!_parser.TryRead(out frame))
        {
            var count = _parser.HasPartialFrame
                ? await ReadWithinFrameDeliveryTimeoutAsync(cancellationToken).ConfigureAwait(false)
                : await ReadBytesAsync(cancellationToken).ConfigureAwait(false);

            if (count == 0)
            {
                return null;
            }

            if (count > 0)
            {
                Append(count);
            }
        }

        return frame;
    }

    private ValueTask<int> ReadBytesAsync(CancellationToken cancellationToken)
    {
        if (_pendingRead is { } pendingRead)
        {
            _pendingRead = null;
            return new ValueTask<int>(pendingRead.WaitAsync(cancellationToken));
        }

        return _stream.ReadAsync(_buffer, cancellationToken);
    }

    // Returns -1 when the partial frame was dropped; the read stays pending in _pendingRead, as canceling it could lose bytes.
    private async ValueTask<int> ReadWithinFrameDeliveryTimeoutAsync(CancellationToken cancellationToken)
    {
        if (_parser.Position != _partialFramePosition)
        {
            _partialFramePosition = _parser.Position;
            _partialFrameStartedAt = _timeProvider.GetTimestamp();
        }

        var remaining = _frameDeliveryTimeout - _timeProvider.GetElapsedTime(_partialFrameStartedAt);
        if (remaining > TimeSpan.Zero)
        {
            var read = _pendingRead ?? StartPendingRead(cancellationToken);
            try
            {
                var count = await read.WaitAsync(remaining, _timeProvider, cancellationToken).ConfigureAwait(false);
                _pendingRead = null;
                return count;
            }
            catch (TimeoutException) when (!read.IsFaulted)
            {
                // The frame delivery timeout expired; the partial frame is dropped below.
            }
        }

        _logger.TryLog(
            LogLevel.Warning,
            null,
            "Dropped an incomplete frame that did not complete within {FrameDeliveryTimeout} ms of its start.",
            _frameDeliveryTimeout.TotalMilliseconds);
        _parser.DropPartialFrame();
        return -1;
    }

    private Task<int> StartPendingRead(CancellationToken cancellationToken)
    {
        var read = _stream.ReadAsync(_buffer, cancellationToken).AsTask();

        // A read abandoned on disposal faults when the stream closes, and nobody awaits it then.
        ObserveException(read);
        _pendingRead = read;
        return read;
    }

    private void Append(int count)
    {
        if (_logger.TryIsEnabled(LogLevel.Trace))
        {
            _logger.TryLog(LogLevel.Trace, null, "Received {Bytes}.", Convert.ToHexString(_buffer, 0, count));
        }

        _parser.Append(_buffer.AsSpan(0, count));
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
            if (_logger.TryIsEnabled(LogLevel.Trace))
            {
                _logger.TryLog(LogLevel.Trace, null, "Sending {Bytes}.", Convert.ToHexString(frame.Span));
            }

            // A stalled network bridge or a hung USB device never completes a write, which would block the driver for good.
            var write = WriteAndFlushAsync(frame, cancellationToken);
            await (write.IsCompleted ? write : WaitForWriteAsync(write, cancellationToken)).ConfigureAwait(false);
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

    private async Task WaitForWriteAsync(Task write, CancellationToken cancellationToken)
    {
        try
        {
            await write.WaitAsync(_writeTimeout, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
        {
            // The write is abandoned, and a write that ignores cancellation faults later when the stream closes.
            ObserveException(write);
            throw;
        }
    }

    private static void ObserveException(Task task)
    {
        if (task.IsCompleted)
        {
            _ = task.Exception;
        }
        else
        {
            task.ContinueWith(
                static completed => _ = completed.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private async Task WriteAndFlushAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken)
    {
        await _stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
