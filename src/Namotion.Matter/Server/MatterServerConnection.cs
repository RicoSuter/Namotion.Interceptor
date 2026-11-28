using System.Globalization;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Namotion.Matter.Server;

/// <summary>
/// One WebSocket connection to a matterjs-server. A single receive loop reads every message, completes the request it
/// answers by <c>message_id</c> and hands each event to the owner. The callbacks run on the receive loop.
/// </summary>
internal sealed class MatterServerConnection : IAsyncDisposable
{
    private readonly TimeSpan _requestTimeout;
    private readonly ILogger? _logger;
    private readonly MatterServerPendingRequests _pendingRequests = new();
    private readonly Lock _disposeLock = new();

    // Never disposed: a request that races DisposeAsync still releases it.
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    // Disposed last in DisposeAsync; a request that reads its token afterwards fails with ObjectDisposedException.
    private readonly CancellationTokenSource _disposeCancellation = new();

    // Received from by ReceiveServerInfoAsync, then only by the receive loop; disposed in DisposeAsync after the loop ended.
    private readonly MatterServerSocket _socket;
    private Action<MatterControllerEvent> _handleEvent = static _ => { };
    private Action<Exception> _handleConnectionLost = static _ => { };
    private Task _receiveLoop = Task.CompletedTask;

    // Set once, by the first failure.
    private volatile Exception? _connectionError;

    // Set once, under the dispose lock.
    private volatile Task? _disposeTask;
    private long _nextMessageId;

    private MatterServerConnection(MatterServerSocket socket, TimeSpan requestTimeout, ILogger? logger)
    {
        _socket = socket;
        _requestTimeout = requestTimeout;
        _logger = logger;
    }

    /// <summary>Opens the WebSocket with the options, which the caller has validated.</summary>
    /// <exception cref="ArgumentException">The URL scheme is not supported.</exception>
    /// <exception cref="WebSocketException">The server refused the connection.</exception>
    public static async Task<MatterServerConnection> ConnectAsync(
        Uri serverUrl, MatterServerOptions options, ILogger? logger, CancellationToken cancellationToken)
    {
        var socket = await MatterServerSocket.ConnectAsync(serverUrl, options, cancellationToken).ConfigureAwait(false);
        return new MatterServerConnection(socket, options.RequestTimeout, logger);
    }

    /// <summary>Receives the information the server sends first; call it once, before <see cref="StartReceiving"/>.</summary>
    /// <exception cref="TimeoutException">The server sent nothing within the request timeout.</exception>
    /// <exception cref="WebSocketException">The server closed the connection first.</exception>
    /// <exception cref="MatterException">The information could not be read.</exception>
    public async Task<MatterServerInfo> ReceiveServerInfoAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_requestTimeout);
        ReadOnlyMemory<byte>? message;
        try
        {
            message = await _socket.ReceiveAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(string.Create(CultureInfo.InvariantCulture,
                $"The Matter server did not send its information within {_requestTimeout}."));
        }

        if (message is not { } serverInfo)
        {
            throw new WebSocketException("The Matter server closed the connection before sending its information.");
        }

        return MatterServerResults.ReadServerInfo(serverInfo);
    }

    /// <summary>Starts the receive loop.</summary>
    /// <param name="handleEvent">Handles an event this client knows.</param>
    /// <param name="handleConnectionLost">Called once with the error when the connection is lost, not when it is disposed.</param>
    public void StartReceiving(Action<MatterControllerEvent> handleEvent, Action<Exception> handleConnectionLost)
    {
        _handleEvent = handleEvent;
        _handleConnectionLost = handleConnectionLost;
        _receiveLoop = Task.Run(RunReceiveLoopAsync, CancellationToken.None);
    }

    /// <summary>Sends a request and returns its cloned <c>result</c>, or the default element when it has none.</summary>
    /// <param name="command">The command.</param>
    /// <param name="writeArguments">Writes the properties of <c>args</c>, or null for a request without arguments.</param>
    /// <param name="timeout">How long to wait for the answer, or null to wait without a limit.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    public async Task<JsonElement> SendRequestAsync(string command, Action<Utf8JsonWriter>? writeArguments, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        var messageId = Interlocked.Increment(ref _nextMessageId).ToString(CultureInfo.InvariantCulture);
        var answer = _pendingRequests.Add(messageId);
        try
        {
            // Closing sets its state before it fails the pending requests, so a request registered after that fails here.
            ThrowIfClosed();
            var message = MatterServerJson.WriteRequest(messageId, command, writeArguments);
            using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCancellation.Token);
            if (timeout is { } requestTimeout)
            {
                waitCancellation.CancelAfter(requestTimeout);
            }

            try
            {
                await SendAsync(command, message, waitCancellation.Token).ConfigureAwait(false);
                return await answer.WaitAsync(waitCancellation.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or WebSocketException && _disposeCancellation.IsCancellationRequested)
            {
                throw new ObjectDisposedException(nameof(MatterServerController));
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(string.Create(CultureInfo.InvariantCulture,
                    $"The Matter server did not answer '{command}' within {timeout}."));
            }
            catch (OperationCanceledException exception) when (exception.CancellationToken != cancellationToken)
            {
                // The wait observed the linked token; the caller sees its own.
                throw new OperationCanceledException(exception.Message, exception, cancellationToken);
            }
        }
        finally
        {
            _pendingRequests.Remove(messageId, answer);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeLock)
        {
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        await _disposeCancellation.CancelAsync().ConfigureAwait(false);
        await _receiveLoop.ConfigureAwait(false);
        _pendingRequests.FailAll(connectionError: null);
        _socket.Dispose();
        _disposeCancellation.Dispose();
    }

    /// <summary>Sends a message under the send lock; a send that takes longer than the request timeout fails the connection.</summary>
    private async Task SendAsync(string command, ReadOnlyMemory<byte> message, CancellationToken waitCancellationToken)
    {
        await _sendLock.WaitAsync(waitCancellationToken).ConfigureAwait(false);
        try
        {
            // Aborting the socket also disposes it, so a request that waited for the lock checks the state first.
            ThrowIfClosed();

            // Canceling a WebSocket send aborts the connection, so the caller's token never reaches the send. A send
            // that times out fails the connection first, so the timeout and not the aborted socket is its error.
            // Disposing the source stops its timer, which also ends the registration.
            using var sendTimeout = new CancellationTokenSource(_requestTimeout);
            _ = sendTimeout.Token.UnsafeRegister(static state => ((MatterServerConnection)state!).FailSendTimedOut(), this);
            try
            {
                await _socket.SendAsync(message, _disposeCancellation.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or WebSocketException or ObjectDisposedException && !_disposeCancellation.IsCancellationRequested)
            {
                if (sendTimeout.IsCancellationRequested)
                {
                    throw new TimeoutException(string.Create(CultureInfo.InvariantCulture,
                        $"The Matter server did not accept '{command}' within {_requestTimeout}."));
                }

                if (_connectionError is { } connectionError)
                {
                    throw MatterServerPendingRequests.CreateClosedException(connectionError);
                }

                throw;
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task RunReceiveLoopAsync()
    {
        Exception error;
        try
        {
            while (true)
            {
                var message = await _socket.ReceiveAsync(_disposeCancellation.Token).ConfigureAwait(false);
                if (message is not { } received)
                {
                    error = new WebSocketException("The Matter server closed the connection.");
                    break;
                }

                if (!HandleMessage(received))
                {
                    error = new WebSocketException("The Matter server shut down.");
                    break;
                }
            }
        }
        catch (Exception exception)
        {
            error = exception;
        }

        if (_disposeCancellation.IsCancellationRequested)
        {
            // Disposing cancels the pending receive, which ends as a cancellation, an aborted socket or a close that raced it.
            _logger?.LogDebug(error, "The Matter server receive loop stopped.");
            return;
        }

        FailConnection(error);
        var connectionError = _connectionError!;
        _logger?.LogWarning(connectionError, "The connection to the Matter server was lost.");
        _handleConnectionLost(connectionError);
    }

    /// <summary>Handles one message; returns false when the server announced its shutdown.</summary>
    private bool HandleMessage(ReadOnlyMemory<byte> message)
    {
        try
        {
            // The document references the socket's buffer, which the next receive overwrites: everything kept is cloned.
            using var document = JsonDocument.Parse(message);
            var root = document.RootElement;
            if (root.TryGetProperty("message_id", out var messageId))
            {
                if (!_pendingRequests.TryComplete(messageId, root))
                {
                    _logger?.LogDebug("Ignored a Matter server response that nothing waits for.");
                }
            }
            else if (root.TryGetProperty("event", out var eventName))
            {
                if (eventName.ValueEquals("server_shutdown"u8))
                {
                    return false;
                }

                if (MatterServerEvents.ReadEvent(eventName, root.GetProperty("data")) is { } controllerEvent)
                {
                    _handleEvent(controllerEvent);
                }
                else
                {
                    _logger?.LogDebug("Ignored the Matter server event {EventName}.", eventName.GetString());
                }
            }
            else
            {
                _logger?.LogDebug("Ignored a Matter server message that is neither a response nor an event.");
            }
        }
        catch (Exception exception) when (MatterServerResults.IsReadFailure(exception))
        {
            _logger?.LogWarning(exception, "Ignored a Matter server message that could not be read.");
        }

        return true;
    }

    private void FailSendTimedOut() => FailConnection(new TimeoutException(string.Create(CultureInfo.InvariantCulture,
        $"The Matter server did not accept a request within {_requestTimeout}.")));

    /// <summary>Closes the connection with its first error; safe to call from any thread, and only the first call has an effect.</summary>
    private void FailConnection(Exception exception)
    {
        if (Interlocked.CompareExchange(ref _connectionError, exception, null) is null)
        {
            // The receive loop sees the aborted socket and reports the loss; it never touches the buffer afterwards.
            _socket.Abort();
            _pendingRequests.FailAll(exception);
        }
    }

    private void ThrowIfClosed()
    {
        ObjectDisposedException.ThrowIf(_disposeTask is not null, typeof(MatterServerController));
        if (_connectionError is { } connectionError)
        {
            throw MatterServerPendingRequests.CreateClosedException(connectionError);
        }
    }
}
