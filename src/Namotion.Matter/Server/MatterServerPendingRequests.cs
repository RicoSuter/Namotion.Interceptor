using System.Collections.Concurrent;
using System.Globalization;
using System.Net.WebSockets;
using System.Text.Json;

namespace Namotion.Matter.Server;

/// <summary>
/// The requests that wait for their answer, by message ID. Whoever removes a request completes it: the receive loop
/// with its answer, closing with an error, or the request itself when it stops waiting.
/// </summary>
internal sealed class MatterServerPendingRequests
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _requests = new();
    private long _nextMessageId;

    /// <summary>Adds a request, whose task completes with its cloned <c>result</c> or fails with its error.</summary>
    /// <param name="messageId">The new request's unique message ID.</param>
    public Task<JsonElement> Add(out string messageId)
    {
        messageId = Interlocked.Increment(ref _nextMessageId).ToString(CultureInfo.InvariantCulture);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _requests[messageId] = completion;
        return completion.Task;
    }

    /// <summary>Removes a request that stopped waiting.</summary>
    /// <param name="messageId">The message ID.</param>
    /// <param name="answer">The request's task, which another thread may complete when it removed the request first.</param>
    public void Remove(string messageId, Task<JsonElement> answer)
    {
        if (_requests.TryRemove(messageId, out _))
        {
            return;
        }

        // The request may no longer await the error another thread sets, so it is observed here.
        if (answer.IsFaulted)
        {
            _ = answer.Exception;
        }
        else if (!answer.IsCompleted)
        {
            answer.ContinueWith(static completed => _ = completed.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    /// <summary>Completes the request a response answers; returns false when nothing waits for it.</summary>
    /// <param name="messageId">The response's <c>message_id</c>.</param>
    /// <param name="response">The response, which is only valid during the call.</param>
    public bool TryComplete(JsonElement messageId, JsonElement response)
    {
        if (!_requests.TryRemove(messageId.GetString() ?? "", out var completion))
        {
            return false;
        }

        try
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
        catch (Exception exception) when (MatterServerResults.IsReadFailure(exception))
        {
            completion.TrySetException(new MatterException("The Matter server's answer could not be read.", exception));
        }

        return true;
    }

    /// <summary>Fails every request with the error that closed the connection, or as disposed when it is null.</summary>
    public void FailAll(Exception? connectionError)
    {
        foreach (var messageId in _requests.Keys)
        {
            if (_requests.TryRemove(messageId, out var completion))
            {
                completion.TrySetException(CreateClosedException(connectionError));
            }
        }
    }

    /// <summary>Creates the exception of one request, so requests never share an exception instance.</summary>
    public static Exception CreateClosedException(Exception? connectionError) => connectionError is null
        ? new ObjectDisposedException(nameof(MatterServerController))
        : new WebSocketException("The connection to the Matter server is closed.", connectionError);
}
