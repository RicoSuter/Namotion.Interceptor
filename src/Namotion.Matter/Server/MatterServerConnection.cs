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

    // Received from only by the receive loop; disposed in DisposeAsync after the loop ended.
    private readonly MatterServerSocket _socket;
    private Action<MatterControllerEvent> _handleEvent = static _ => { };
    private Action<Exception> _handleConnectionLost = static _ => { };
    private Task _receiveLoop = Task.CompletedTask;

    // Set once, by the first failure.
    private volatile Exception? _connectionError;

    // Set once, under the dispose lock.
    private volatile Task? _disposeTask;

    // 1 while a send runs, so a send timer that fires once the send completed does nothing.
    private int _sendInProgress;

    private MatterServerConnection(MatterServerSocket socket, MatterServerInfo serverInfo, TimeSpan requestTimeout, ILogger? logger)
    {
        _socket = socket;
        ServerInfo = serverInfo;
        _requestTimeout = requestTimeout;
        _logger = logger;
    }

    /// <summary>
    /// Opens the WebSocket and receives the information the server sends first, both within
    /// <see cref="MatterServerOptions.ConnectTimeout"/>; the options are validated by the caller.
    /// </summary>
    /// <exception cref="ArgumentException">The URL scheme is not supported.</exception>
    /// <exception cref="WebSocketException">The server refused the connection or closed it before sending its information.</exception>
    /// <exception cref="MatterException">The information could not be read.</exception>
    /// <exception cref="TimeoutException">Connecting took longer than the connect timeout.</exception>
    public static async Task<MatterServerConnection> ConnectAsync(
        Uri serverUrl, MatterServerOptions options, ILogger? logger, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.ConnectTimeout);
        try
        {
            var socket = await MatterServerSocket.ConnectAsync(serverUrl, options, timeout.Token).ConfigureAwait(false);
            try
            {
                var message = await socket.ReceiveAsync(timeout.Token).ConfigureAwait(false)
                    ?? throw new WebSocketException("The Matter server closed the connection before sending its information.");
                return new MatterServerConnection(socket, MatterServerResults.ReadServerInfo(message), options.RequestTimeout, logger);
            }
            catch
            {
                // The receive has ended, so the buffer can be returned.
                socket.Dispose();
                throw;
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or WebSocketException &&
                                          timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"The Matter server did not accept the connection and send its information within {options.ConnectTimeout}.", exception);
        }
    }

    /// <summary>Gets the information the server sent when the connection opened.</summary>
    public MatterServerInfo ServerInfo { get; }

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
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="withoutTimeout">Whether to wait for the answer without the request timeout, which only commissioning does.</param>
    public async Task<JsonElement> SendRequestAsync(
        string command, Action<Utf8JsonWriter>? writeArguments, CancellationToken cancellationToken, bool withoutTimeout = false)
    {
        var answer = _pendingRequests.Add(out var messageId);
        try
        {
            // Closing sets its state before it fails the pending requests, so a request registered after that fails here.
            ThrowIfClosed();
            var message = MatterServerJson.WriteRequest(messageId, command, writeArguments);
            using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCancellation.Token);
            if (!withoutTimeout)
            {
                waitCancellation.CancelAfter(_requestTimeout);
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
                throw new TimeoutException($"The Matter server did not answer '{command}' within {_requestTimeout}.");
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
            using var sendTimeout = new CancellationTokenSource(_requestTimeout);
            Interlocked.Exchange(ref _sendInProgress, 1);

            // Disposing the registration before the lock is released waits for a running timeout callback.
            using (sendTimeout.Token.UnsafeRegister(static state => ((MatterServerConnection)state!).FailSendTimedOut(), this))
            {
                try
                {
                    await _socket.SendAsync(message, _disposeCancellation.Token).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is OperationCanceledException or WebSocketException or ObjectDisposedException && !_disposeCancellation.IsCancellationRequested)
                {
                    if (Interlocked.Exchange(ref _sendInProgress, 0) == 0)
                    {
                        throw new TimeoutException($"The Matter server did not accept '{command}' within {_requestTimeout}.");
                    }

                    // Whether the receive loop recorded the failure first, the request fails like every other one.
                    FailConnection(new WebSocketException("Sending to the Matter server failed.", exception));
                    throw MatterServerPendingRequests.CreateClosedException(_connectionError);
                }
                finally
                {
                    Interlocked.Exchange(ref _sendInProgress, 0);
                }
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
        catch (OperationCanceledException exception) when (!_disposeCancellation.IsCancellationRequested)
        {
            // An aborted socket, for example after a keep-alive timeout, ends the receive as a cancellation, which a
            // consumer would take for its own; the error inside says why.
            error = new WebSocketException("The connection to the Matter server was aborted.", exception);
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
                return HandleEvent(eventName, root);
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

    /// <summary>Handles an event message; returns false when the server announced its shutdown.</summary>
    private bool HandleEvent(JsonElement eventName, JsonElement root)
    {
        if (eventName.ValueEquals("server_shutdown"u8))
        {
            return false;
        }

        if (MatterServerEvents.ReadEvent(eventName, root.GetProperty("data")) is { } controllerEvent)
        {
            _handleEvent(controllerEvent);
        }
        else if (_logger?.IsEnabled(LogLevel.Debug) == true)
        {
            _logger.LogDebug("Ignored the Matter server event {EventName}.", eventName.GetString());
        }

        return true;
    }

    /// <summary>Fails the connection unless the send completed meanwhile or the connection is being disposed.</summary>
    private void FailSendTimedOut()
    {
        if (Interlocked.CompareExchange(ref _sendInProgress, 0, 1) == 1 && !_disposeCancellation.IsCancellationRequested)
        {
            FailConnection(new TimeoutException($"The Matter server did not accept a request within {_requestTimeout}."));
        }
    }

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
