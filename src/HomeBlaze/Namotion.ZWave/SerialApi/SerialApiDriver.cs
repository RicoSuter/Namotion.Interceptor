using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Namotion.ZWave.SerialApi;

/// <summary>
/// Runs the Serial API over a stream: one transaction in flight at a time, ACKs for every received data frame,
/// and unsolicited request frames handed to <see cref="UnsolicitedFrames"/>.
/// </summary>
internal sealed class SerialApiDriver : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly FrameStream _frames;
    private readonly ZWaveControllerOptions _options;
    private readonly ILogger _logger;
    private readonly Channel<SerialApiTransaction> _transactions = Channel.CreateUnbounded<SerialApiTransaction>();
    private readonly Channel<DataFrame> _unsolicitedFrames = Channel.CreateUnbounded<DataFrame>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly CancellationTokenSource _stopping = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Written by the send loop before a frame is written, completed by the read loop.
    private volatile TaskCompletionSource<FrameKind>? _pendingAcknowledgement;
    private volatile PendingResponse? _pendingResponse;

    private volatile ZWaveConnectionException? _failure;
    private Task _readLoop = Task.CompletedTask;
    private Task _sendLoop = Task.CompletedTask;
    private volatile int _disposed;

    public SerialApiDriver(Stream stream, ZWaveControllerOptions options, ILogger logger)
    {
        _stream = stream;
        _frames = new FrameStream(stream, logger);
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Completes when the driver is disposed, and faults with <see cref="ZWaveConnectionException"/> when the connection fails.
    /// </summary>
    public Task Completion => _completion.Task;

    public ChannelReader<DataFrame> UnsolicitedFrames => _unsolicitedFrames.Reader;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _readLoop = Task.Run(ReadLoopAsync, CancellationToken.None);

        // A NAK makes the controller drop a frame it had partially received, so both sides start in sync.
        await WriteAsync(FrameStream.Nak, cancellationToken).ConfigureAwait(false);

        _sendLoop = Task.Run(SendLoopAsync, CancellationToken.None);
    }

    /// <summary>
    /// Sends a request and returns the controller's response frame.
    /// </summary>
    public Task<DataFrame> SendRequestAsync(byte function, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken) =>
        Enqueue(function, payload, expectsResponse: true, cancellationToken);

    /// <summary>
    /// Sends a request that the controller only acknowledges.
    /// </summary>
    public Task SendCommandAsync(byte function, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken) =>
        Enqueue(function, payload, expectsResponse: false, cancellationToken);

    private Task<DataFrame> Enqueue(byte function, ReadOnlyMemory<byte> payload, bool expectsResponse, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);

        var transaction = new SerialApiTransaction(function, FrameEncoder.Encode(FrameType.Request, function, payload.Span), expectsResponse, cancellationToken);
        if (!_transactions.Writer.TryWrite(transaction))
        {
            transaction.Fail((Exception?)_failure ?? new ObjectDisposedException(nameof(SerialApiDriver)));
        }

        return transaction.Task;
    }

    private async Task SendLoopAsync()
    {
        var stopping = _stopping.Token;
        try
        {
            await foreach (var transaction in _transactions.Reader.ReadAllAsync(stopping).ConfigureAwait(false))
            {
                if (transaction.IsCompleted)
                {
                    continue;
                }

                if (_failure is { } failure)
                {
                    transaction.Fail(failure);
                    continue;
                }

                try
                {
                    transaction.Complete(await ExecuteAsync(transaction, stopping).ConfigureAwait(false));
                }
                catch (Exception) when (stopping.IsCancellationRequested)
                {
                    // Also a write that a closed stream ended with an I/O error: the caller sees why the driver stopped.
                    transaction.Fail((Exception?)_failure ?? new ObjectDisposedException(nameof(SerialApiDriver)));
                    return;
                }
                catch (Exception exception)
                {
                    transaction.Fail(exception);
                }
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            // Disposed or failed while waiting for the next transaction.
        }
    }

    private async Task<DataFrame> ExecuteAsync(SerialApiTransaction transaction, CancellationToken stopping)
    {
        var response = transaction.ExpectsResponse
            ? new TaskCompletionSource<DataFrame>(TaskCreationOptions.RunContinuationsAsynchronously)
            : null;
        _pendingResponse = response is null ? null : new PendingResponse(transaction.Function, response);
        try
        {
            await WriteUntilAcknowledgedAsync(transaction, stopping).ConfigureAwait(false);
            if (response is null)
            {
                return default;
            }

            return await WaitOrDefaultAsync(response.Task, _options.ResponseTimeout, stopping).ConfigureAwait(false)
                ?? throw new ZWaveTimeoutException($"The Z-Wave controller did not answer function 0x{transaction.Function:X2}.");
        }
        finally
        {
            _pendingAcknowledgement = null;
            _pendingResponse = null;
        }
    }

    private async Task WriteUntilAcknowledgedAsync(SerialApiTransaction transaction, CancellationToken stopping)
    {
        var attempts = _options.MaximumSendAttempts;
        FrameKind? result = null;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            var acknowledgement = new TaskCompletionSource<FrameKind>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingAcknowledgement = acknowledgement;

            await WriteAsync(transaction.Frame, stopping).ConfigureAwait(false);
            result = await WaitOrDefaultAsync(acknowledgement.Task, _options.AcknowledgementTimeout, stopping).ConfigureAwait(false);
            if (result == FrameKind.Ack)
            {
                return;
            }

            if (attempt < attempts)
            {
                _logger.LogDebug("Retransmitting function 0x{Function:X2} after {Result}.", transaction.Function, result?.ToString() ?? "a timeout");
                await Task.Delay(TimeSpan.FromMilliseconds(100 + (attempt - 1) * 1000), _options.TimeProvider, stopping).ConfigureAwait(false);
            }
        }

        throw result is null
            ? new ZWaveTimeoutException($"The Z-Wave controller did not acknowledge function 0x{transaction.Function:X2} after {attempts} attempts.")
            : new ZWaveControllerException($"The Z-Wave controller answered function 0x{transaction.Function:X2} with {result} after {attempts} attempts.");
    }

    private async Task ReadLoopAsync()
    {
        var stopping = _stopping.Token;
        try
        {
            while (await _frames.ReadAsync(stopping).ConfigureAwait(false) is { } frame)
            {
                await HandleFrameAsync(frame, stopping).ConfigureAwait(false);
            }

            if (!stopping.IsCancellationRequested)
            {
                Fail(new ZWaveConnectionException("The connection to the Z-Wave controller was closed."));
            }
        }
        catch (Exception exception) when (!stopping.IsCancellationRequested)
        {
            Fail(exception as ZWaveConnectionException ?? new ZWaveConnectionException("Reading from the Z-Wave controller failed.", exception));
        }
        catch (Exception) when (stopping.IsCancellationRequested)
        {
            // Disposed or failed; disposing also closes the stream under a pending read.
        }
    }

    private async ValueTask HandleFrameAsync(ParsedFrame frame, CancellationToken stopping)
    {
        switch (frame.Kind)
        {
            case FrameKind.Ack:
            case FrameKind.Nak:
            case FrameKind.Can:
                if (_pendingAcknowledgement is { } acknowledgement)
                {
                    acknowledgement.TrySetResult(frame.Kind);
                }
                else
                {
                    _logger.LogDebug("Received an unexpected {Kind}.", frame.Kind);
                }

                break;

            case FrameKind.ChecksumError:
                _logger.LogWarning("Received a frame with a wrong checksum.");
                await WriteAsync(FrameStream.Nak, stopping).ConfigureAwait(false);
                break;

            case FrameKind.Data:
                await WriteAsync(FrameStream.Ack, stopping).ConfigureAwait(false);
                var data = frame.Data;
                if (data.Type == FrameType.Response)
                {
                    if (_pendingResponse is { } pending && pending.Function == data.Function)
                    {
                        pending.Source.TrySetResult(data);
                    }
                    else
                    {
                        _logger.LogDebug("Received an unexpected response to function 0x{Function:X2}.", data.Function);
                    }
                }
                else
                {
                    _unsolicitedFrames.Writer.TryWrite(data);
                }

                break;
        }
    }

    private async Task WriteAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken)
    {
        try
        {
            await _frames.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        }
        catch (ZWaveConnectionException failure)
        {
            Fail(failure);
            throw;
        }
    }

    private async Task<T?> WaitOrDefaultAsync<T>(Task<T> task, TimeSpan timeout, CancellationToken cancellationToken)
        where T : struct
    {
        try
        {
            return await task.WaitAsync(timeout, _options.TimeProvider, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    private void Fail(ZWaveConnectionException failure)
    {
        if (Interlocked.CompareExchange(ref _failure, failure, null) is not null)
        {
            return;
        }

        _logger.LogWarning(failure, "The connection to the Z-Wave controller failed.");
        _transactions.Writer.TryComplete();
        _pendingAcknowledgement?.TrySetException(failure);
        _pendingResponse?.Source.TrySetException(failure);

        // Wakes the send loop in a retransmission delay, or one that started an attempt after the pending sources were read.
        _stopping.Cancel();
        while (_transactions.Reader.TryRead(out var transaction))
        {
            transaction.Fail(failure);
        }

        _unsolicitedFrames.Writer.TryComplete();
        _completion.TrySetException(failure);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _transactions.Writer.TryComplete();
        await _stopping.CancelAsync().ConfigureAwait(false);

        // A serial read does not always observe cancellation; closing the stream ends it.
        try
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Closing an unplugged serial port can throw; the loops still end through the canceled token.
            _logger.LogWarning(exception, "Closing the connection to the Z-Wave controller failed.");
        }

        await Task.WhenAll(_readLoop, _sendLoop).ConfigureAwait(false);

        var disposed = new ObjectDisposedException(nameof(SerialApiDriver));
        while (_transactions.Reader.TryRead(out var transaction))
        {
            transaction.Fail(disposed);
        }

        _unsolicitedFrames.Writer.TryComplete();
        _completion.TrySetResult();
        _stopping.Dispose();
        _frames.Dispose();
    }

    private sealed record PendingResponse(byte Function, TaskCompletionSource<DataFrame> Source);
}
