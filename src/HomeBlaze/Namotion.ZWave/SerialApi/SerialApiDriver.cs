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
        new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _stopping = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Start and the start of disposal are exclusive, so that disposal always awaits the loops Start created.
    private readonly Lock _lifecycleLock = new();

    // Set by the send loop before its frame is written; the read loop completes its acknowledgement and response.
    private volatile SerialApiTransaction? _inFlight;

    private volatile ZWaveConnectionException? _failure;
    private Task _readLoop = Task.CompletedTask;
    private Task _sendLoop = Task.CompletedTask;
    private volatile bool _disposed;
    private bool _started;
    private Task? _disposal;

    // Closes the stream once, on a pool thread, for the first of Fail and disposal; the other awaits the same close.
    private readonly Lazy<Task> _streamClosing;

    public SerialApiDriver(Stream stream, ZWaveControllerOptions options, ILogger logger)
    {
        _stream = stream;
        _streamClosing = new Lazy<Task>(() => Task.Run(CloseStreamAsync, CancellationToken.None));
        _frames = new FrameStream(stream, options, logger);
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Completes when the driver is disposed, and faults with <see cref="ZWaveConnectionException"/> when the connection
    /// fails; the driver then closes the stream by itself, and disposing awaits that close.
    /// </summary>
    public Task Completion => _completion.Task;

    /// <summary>
    /// The request frames the controller sent on its own, in their order. Completes when the driver fails or is disposed.
    /// </summary>
    /// <remarks>
    /// The channel is unbounded: its single reader must keep up and must never block, or the frames accumulate in memory.
    /// </remarks>
    public ChannelReader<DataFrame> UnsolicitedFrames => _unsolicitedFrames.Reader;

    /// <summary>
    /// Starts the read and send loops. The send loop first writes a NAK; when that fails, <see cref="Completion"/> faults.
    /// </summary>
    /// <exception cref="InvalidOperationException">The driver was already started.</exception>
    /// <exception cref="ObjectDisposedException">The driver was disposed.</exception>
    public void Start()
    {
        lock (_lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
            {
                throw new InvalidOperationException("The Z-Wave driver was already started.");
            }

            _started = true;
            _readLoop = Task.Run(ReadLoopAsync, CancellationToken.None);
            _sendLoop = Task.Run(SendLoopAsync, CancellationToken.None);
        }
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
        ObjectDisposedException.ThrowIf(_disposed, this);

        var transaction = new SerialApiTransaction(function, FrameEncoder.Encode(FrameType.Request, function, payload.Span), expectsResponse, cancellationToken);
        if (!_transactions.Writer.TryWrite(transaction))
        {
            transaction.Fail(CreateStoppedException());
        }

        return transaction.Task;
    }

    private async Task SendLoopAsync()
    {
        var stopping = _stopping.Token;
        try
        {
            // A NAK makes the controller drop a frame it had partially received, so both sides start in sync.
            await WriteAsync(FrameStream.Nak, stopping).ConfigureAwait(false);

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
                    transaction.Fail(CreateStoppedException());
                    return;
                }
                catch (Exception exception)
                {
                    transaction.Fail(exception);
                }
            }
        }
        catch (Exception) when (stopping.IsCancellationRequested || _failure is not null)
        {
            // Disposed or failed while writing the startup NAK or waiting for the next transaction. A Fail that lost the race
            // to another loop's Fail returns before that one has canceled the token, so the stored failure counts as well.
        }
    }

    private async Task<DataFrame> ExecuteAsync(SerialApiTransaction transaction, CancellationToken stopping)
    {
        _inFlight = transaction;
        try
        {
            await WriteUntilAcknowledgedAsync(transaction, stopping).ConfigureAwait(false);
            if (transaction.Response is not { } response)
            {
                return default;
            }

            return await WaitOrDefaultAsync(response, _options.ResponseTimeout, stopping).ConfigureAwait(false)
                ?? throw new ZWaveTimeoutException($"The Z-Wave controller did not answer function 0x{transaction.Function:X2}.");
        }
        finally
        {
            _inFlight = null;
        }
    }

    private async Task WriteUntilAcknowledgedAsync(SerialApiTransaction transaction, CancellationToken stopping)
    {
        var attempts = _options.MaximumSendAttempts;
        FrameKind? result = null;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            var acknowledgement = transaction.StartAttempt();
            await WriteAsync(transaction.Frame, stopping).ConfigureAwait(false);
            result = await WaitOrDefaultAsync(acknowledgement, _options.AcknowledgementTimeout, stopping).ConfigureAwait(false);
            if (result == FrameKind.Ack)
            {
                return;
            }

            if (result is null)
            {
                transaction.ExpireAttempt();
            }

            if (attempt < attempts)
            {
                _logger.TryLog(LogLevel.Debug, null, "Retransmitting function 0x{Function:X2} after {Result}.", transaction.Function, result?.ToString() ?? "a timeout");
                await Task.Delay(TimeSpan.FromMilliseconds(100 + (attempt - 1) * 1000), _options.TimeProvider, stopping).ConfigureAwait(false);
            }
        }

        // An attempt the controller answered after it timed out also counts: such a controller is slow, not gone.
        if (!transaction.WasAnswered)
        {
            // A controller that answers no attempt at all is gone, so its owner must reconnect.
            var failure = new ZWaveConnectionException(
                $"The Z-Wave controller stopped responding: it answered none of the {attempts} attempts of function 0x{transaction.Function:X2}.");
            Fail(failure);
            throw failure;
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
            var failure = await ReadUntilFailureAsync(stopping).ConfigureAwait(false);
            if (!stopping.IsCancellationRequested)
            {
                Fail(failure);
            }
        }
        catch (Exception) when (stopping.IsCancellationRequested)
        {
            // Disposed or failed; disposing also closes the stream under a pending read.
        }
    }

    private async Task<ZWaveConnectionException> ReadUntilFailureAsync(CancellationToken stopping)
    {
        try
        {
            while (await _frames.ReadAsync(stopping).ConfigureAwait(false) is { } frame)
            {
                await HandleFrameAsync(frame, stopping).ConfigureAwait(false);
            }

            return new ZWaveConnectionException("The connection to the Z-Wave controller was closed.");
        }
        catch (Exception exception) when (!stopping.IsCancellationRequested)
        {
            return exception as ZWaveConnectionException ?? new ZWaveConnectionException("Reading from the Z-Wave controller failed.", exception);
        }
    }

    private async ValueTask HandleFrameAsync(ParsedFrame frame, CancellationToken stopping)
    {
        switch (frame.Kind)
        {
            case FrameKind.Ack:
            case FrameKind.Nak:
            case FrameKind.Can:
                HandleAcknowledgement(frame.Kind);
                break;

            case FrameKind.ChecksumError:
                _logger.TryLog(LogLevel.Warning, null, "Received a frame with a wrong checksum.");
                await WriteAsync(FrameStream.Nak, stopping).ConfigureAwait(false);
                break;

            case FrameKind.Data:
                await WriteAsync(FrameStream.Ack, stopping).ConfigureAwait(false);
                RouteDataFrame(frame.Data);
                break;
        }
    }

    private void HandleAcknowledgement(FrameKind kind)
    {
        var transaction = _inFlight;
        switch (transaction?.TryAcknowledge(kind) ?? AcknowledgementMatch.None)
        {
            case AcknowledgementMatch.Late:
                _logger.TryLog(LogLevel.Debug, null, "Received a late {Kind} to function 0x{Function:X2} after its attempt timed out.", kind, transaction!.Function);
                break;

            case AcknowledgementMatch.None:
                _logger.TryLog(LogLevel.Debug, null, "Received an unexpected {Kind}.", kind);
                break;
        }
    }

    private void RouteDataFrame(DataFrame data)
    {
        if (data.Type != FrameType.Response)
        {
            _unsolicitedFrames.Writer.TryWrite(data);
        }
        else if (_inFlight?.TryRespond(data) != true)
        {
            _logger.TryLog(LogLevel.Debug, null, "Received an unexpected response to function 0x{Function:X2}.", data.Function);
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

    private Exception CreateStoppedException() =>
        (Exception?)_failure ?? new ObjectDisposedException(nameof(SerialApiDriver));

    private void Fail(ZWaveConnectionException failure)
    {
        if (Interlocked.CompareExchange(ref _failure, failure, null) is not null)
        {
            return;
        }

        _transactions.Writer.TryComplete();
        _inFlight?.FailExchange(failure);

        // Wakes the send loop in a retransmission delay, or one that started an attempt after the pending sources were read.
        _stopping.Cancel();
        while (_transactions.Reader.TryRead(out var transaction))
        {
            transaction.Fail(failure);
        }

        _unsolicitedFrames.Writer.TryComplete();
        _completion.TrySetException(failure);

        // The failure is logged, so it is observed here; an owner that awaits Completion still gets it.
        _ = _completion.Task.Exception;

        // Releases the port also when the owner does not dispose; the loops end through the canceled token.
        _ = _streamClosing.Value;

        _logger.TryLog(LogLevel.Warning, failure, "The connection to the Z-Wave controller failed.");
    }

    /// <summary>
    /// Stops the loops, closes the stream and fails the requests that did not complete. Every call returns the same
    /// disposal, which completes once it finished.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        lock (_lifecycleLock)
        {
            if (_disposal is null)
            {
                _disposed = true;
                _disposal = DisposeCoreAsync();
            }

            return new ValueTask(_disposal);
        }
    }

    private async Task DisposeCoreAsync()
    {
        _transactions.Writer.TryComplete();
        await _stopping.CancelAsync().ConfigureAwait(false);

        // A serial read does not always observe cancellation; closing the stream ends it.
        await _streamClosing.Value.ConfigureAwait(false);

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

    private async Task CloseStreamAsync()
    {
        try
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Closing an unplugged serial port can throw; the loops still end through the canceled token.
            _logger.TryLog(LogLevel.Warning, exception, "Closing the connection to the Z-Wave controller failed.");
        }
    }
}
