using System.Collections.Concurrent;
using System.Globalization;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Namotion.Matter.Server;

/// <summary>
/// One WebSocket connection to a matterjs-server. A single receive loop reads every message, completes the request it
/// answers by <c>message_id</c> and hands each event to the owner. The callbacks run on the receive loop.
/// </summary>
internal sealed class MatterServerConnection : IAsyncDisposable
{
    private const string ServerShutdownEvent = "server_shutdown";

    private readonly TimeSpan _requestTimeout;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pendingRequests = new();

    // Never disposed: a request that races DisposeAsync still releases it.
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    // Disposed last in DisposeAsync; a request that reads its token afterwards fails with ObjectDisposedException.
    private readonly CancellationTokenSource _disposeCancellation = new();

    // Read by ReceiveServerInfoAsync, then only by the receive loop; disposed in DisposeAsync after the loop ended.
    private readonly MatterServerSocket _socket;
    private Func<string, JsonElement, bool> _handleEvent = static (_, _) => false;
    private Action<Exception> _handleConnectionLost = static _ => { };
    private Task _receiveLoop = Task.CompletedTask;
    private volatile Exception? _connectionError;
    private volatile int _disposed;
    private long _nextMessageId;

    private MatterServerConnection(MatterServerSocket socket, TimeSpan requestTimeout, ILogger logger)
    {
        _socket = socket;
        _requestTimeout = requestTimeout;
        _logger = logger;
    }

    /// <summary>Opens the WebSocket; see <see cref="MatterServerOptions"/> for the timeout and the maximum message size.</summary>
    /// <exception cref="ArgumentException">The URL scheme is not supported.</exception>
    public static async Task<MatterServerConnection> ConnectAsync(
        Uri serverUrl, TimeSpan requestTimeout, int maxMessageSize, ILogger? logger, CancellationToken cancellationToken)
    {
        var socket = await MatterServerSocket.ConnectAsync(serverUrl, maxMessageSize, cancellationToken).ConfigureAwait(false);
        return new MatterServerConnection(socket, requestTimeout, logger ?? NullLogger.Instance);
    }

    /// <summary>Receives the information the server sends first; call it once, before <see cref="StartReceiving"/>.</summary>
    /// <exception cref="TimeoutException">The server sent nothing within the request timeout.</exception>
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

        using var document = JsonDocument.Parse(serverInfo);
        return MatterServerJson.ReadServerInfo(document.RootElement);
    }

    /// <summary>Starts the receive loop.</summary>
    /// <param name="handleEvent">Handles an event by name and returns whether it knows it; the data is only valid during the call.</param>
    /// <param name="handleConnectionLost">Called once with the error when the connection is lost, not when it is disposed.</param>
    public void StartReceiving(Func<string, JsonElement, bool> handleEvent, Action<Exception> handleConnectionLost)
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
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRequests[messageId] = completion;
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
                await _sendLock.WaitAsync(waitCancellation.Token).ConfigureAwait(false);
                try
                {
                    // Canceling a WebSocket send aborts the connection, so the send only stops when the connection is disposed.
                    await _socket.SendAsync(message, _disposeCancellation.Token).ConfigureAwait(false);
                }
                finally
                {
                    _sendLock.Release();
                }

                return await completion.Task.WaitAsync(waitCancellation.Token).ConfigureAwait(false);
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
        }
        finally
        {
            _pendingRequests.TryRemove(messageId, out _);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _disposeCancellation.CancelAsync().ConfigureAwait(false);
        await _receiveLoop.ConfigureAwait(false);
        FailPendingRequests(connectionError: null);
        _socket.Dispose();
        _disposeCancellation.Dispose();
    }

    private async Task RunReceiveLoopAsync()
    {
        try
        {
            while (true)
            {
                var message = await _socket.ReceiveAsync(_disposeCancellation.Token).ConfigureAwait(false);
                if (message is not { } received)
                {
                    CloseWithError(new WebSocketException("The Matter server closed the connection."));
                    return;
                }

                if (!HandleMessage(received))
                {
                    CloseWithError(new WebSocketException("The Matter server shut down."));
                    return;
                }
            }
        }
        catch (Exception exception) when (_disposeCancellation.IsCancellationRequested)
        {
            // Disposing cancels the pending receive, which ends as a cancellation or as an aborted socket.
            _logger.LogDebug(exception, "The Matter server receive loop stopped.");
        }
        catch (Exception exception)
        {
            CloseWithError(exception);
        }
    }

    /// <summary>Handles one message; returns false when the server announced its shutdown.</summary>
    private bool HandleMessage(ReadOnlyMemory<byte> message)
    {
        TaskCompletionSource<JsonElement>? completion = null;
        try
        {
            // The document references the socket's buffer, which the next receive overwrites: everything kept is cloned.
            using var document = JsonDocument.Parse(message);
            var root = document.RootElement;
            if (root.TryGetProperty("message_id", out var messageId))
            {
                if (!_pendingRequests.TryRemove(messageId.GetString() ?? "", out completion))
                {
                    _logger.LogDebug("Ignored a Matter server response that nothing waits for.");
                    return true;
                }

                CompleteRequest(completion, root);
            }
            else if (root.TryGetProperty("event", out var eventName))
            {
                var name = eventName.GetString() ?? "";
                if (name == ServerShutdownEvent)
                {
                    return false;
                }

                if (!_handleEvent(name, root.GetProperty("data")))
                {
                    _logger.LogDebug("Ignored the Matter server event {EventName}.", name);
                }
            }
            else
            {
                _logger.LogDebug("Ignored a Matter server message that is neither a response nor an event.");
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (completion is null)
            {
                _logger.LogWarning(exception, "Ignored a Matter server message that could not be read.");
            }
            else
            {
                completion.TrySetException(new MatterException("The Matter server's answer could not be read.", exception));
            }
        }

        return true;
    }

    private static void CompleteRequest(TaskCompletionSource<JsonElement> completion, JsonElement response)
    {
        if (response.TryGetProperty("error_code", out _))
        {
            completion.TrySetException(MatterServerJson.ReadError(response));
        }
        else
        {
            completion.TrySetResult(response.TryGetProperty("result", out var result) ? result.Clone() : default);
        }
    }

    private void CloseWithError(Exception exception)
    {
        _logger.LogWarning(exception, "The connection to the Matter server was lost.");
        _connectionError = exception;
        FailPendingRequests(exception);
        _handleConnectionLost(exception);
    }

    private void FailPendingRequests(Exception? connectionError)
    {
        foreach (var messageId in _pendingRequests.Keys)
        {
            if (_pendingRequests.TryRemove(messageId, out var completion))
            {
                completion.TrySetException(CreateClosedException(connectionError));
            }
        }
    }

    private void ThrowIfClosed()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, typeof(MatterServerController));
        if (_connectionError is { } connectionError)
        {
            throw CreateClosedException(connectionError);
        }
    }

    /// <summary>Creates the exception of one request, so requests never share an exception instance.</summary>
    private static Exception CreateClosedException(Exception? connectionError) => connectionError is null
        ? new ObjectDisposedException(nameof(MatterServerController))
        : new WebSocketException("The connection to the Matter server is closed.", connectionError);
}
