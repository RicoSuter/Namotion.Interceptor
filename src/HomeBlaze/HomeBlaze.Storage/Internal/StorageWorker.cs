using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace HomeBlaze.Storage.Internal;

/// <summary>
/// Runs work items one at a time, in the order they were handed in. Everything that changes the subject tree
/// or the index of a storage goes through it, which is why neither needs a lock.
/// </summary>
internal sealed class StorageWorker : IDisposable
{
    internal const string CallFromWithinItemMessage =
        "An operation of a storage was called from work that runs on the worker of that storage, such as the load " +
        "of a file or the ApplyConfigurationAsync of a subject. The operation would wait for the work that waits " +
        "for it. Start it once that work has returned, from a task that is started without the execution flow " +
        "(ExecutionContext.SuppressFlow).";

    private readonly Channel<WorkItem> _queue =
        Channel.CreateUnbounded<WorkItem>(new UnboundedChannelOptions { SingleReader = true });

    private readonly CancellationTokenSource _disposalSource = new();
    private readonly AsyncLocal<WorkItem?> _itemOfFlow = new();
    private readonly ILogger? _logger;
    private readonly Action? _whenStopped;
    private readonly Task _loop;

    /// <param name="logger">Receives failures that no caller gets.</param>
    /// <param name="whenStopped">Runs on the worker once, after its last item.</param>
    public StorageWorker(ILogger? logger = null, Action? whenStopped = null)
    {
        _logger = logger;
        _whenStopped = whenStopped;

        // Created within an item of another worker, the loop would otherwise carry the flow of that item into
        // everything it runs.
        using (ExecutionContext.SuppressFlow())
        {
            _loop = Task.Run(ProcessQueueAsync);
        }
    }

    /// <summary>
    /// Runs the work after everything handed in before it. After disposal the returned task is cancelled.
    /// </summary>
    /// <remarks>
    /// A call from within a running item of this worker is refused: the work is not run, and the returned task fails with an <see cref="InvalidOperationException"/>. Every flow that the item started counts as within it while the work of the item has not returned, also a flow the item does not await, such as a task or a timer it started. Code that runs on the worker therefore starts such work without the execution flow (<see cref="ExecutionContext.SuppressFlow"/>). Once the item has finished, a call from a flow it started is queued like any other.
    /// </remarks>
    public Task<TResult> RunAsync<TResult>(Func<CancellationToken, Task<TResult>> work, CancellationToken cancellationToken)
    {
        // Queued, the call would wait for the item that waits for it, and nothing would ever run again. That
        // the flow carries an item is not enough: a task that an item started keeps the flow of that item
        // after it has finished.
        if (_itemOfFlow.Value is { IsFinished: false })
        {
            return Task.FromException<TResult>(new InvalidOperationException(CallFromWithinItemMessage));
        }

        var item = new WorkItem<TResult>(work, cancellationToken);
        if (!_queue.Writer.TryWrite(item))
        {
            item.Cancel(_disposalSource.Token);
        }

        return item.Completion;
    }

    /// <inheritdoc cref="RunAsync{TResult}"/>
    public Task RunAsync(Func<CancellationToken, Task> work, CancellationToken cancellationToken)
        => RunAsync(async token =>
        {
            await work(token);
            return true;
        }, cancellationToken);

    private async Task ProcessQueueAsync()
    {
        await foreach (var item in _queue.Reader.ReadAllAsync())
        {
            _itemOfFlow.Value = item;
            try
            {
                await item.ExecuteAsync(_disposalSource.Token);
            }
            catch (Exception exception)
            {
                // An item reports its own outcome to its caller. Reaching this means that reporting failed.
                _logger?.LogError(exception, "Storage work item failed outside of its own error handling");
            }
        }

        try
        {
            _whenStopped?.Invoke();
        }
        catch (Exception exception)
        {
            _logger?.LogError(exception, "Storage worker failed after its last item");
        }
    }

    /// <summary>
    /// Lets the running item finish and cancels the items that are still queued.
    /// </summary>
    public void Dispose()
    {
        _disposalSource.Cancel();
        _queue.Writer.TryComplete();
    }

    /// <summary>
    /// Does what <see cref="Dispose"/> does and completes when the loop has ended: the running item has
    /// finished, and what the worker does when it has stopped is done. Called from within a running item of
    /// this worker, it completes at once.
    /// </summary>
    public Task StopAsync()
    {
        Dispose();

        // The loop ends after the item of this flow, which would wait for itself.
        return _itemOfFlow.Value is { IsFinished: false } ? Task.CompletedTask : _loop;
    }

    private abstract class WorkItem
    {
        private volatile bool _isFinished;

        /// <summary>True once the work of the item has returned, or when it was not run at all.</summary>
        public bool IsFinished => _isFinished;

        public abstract Task ExecuteAsync(CancellationToken disposalToken);

        protected void MarkFinished() => _isFinished = true;
    }

    private sealed class WorkItem<TResult>(
        Func<CancellationToken, Task<TResult>> work,
        CancellationToken cancellationToken) : WorkItem
    {
        // Asynchronous continuations keep the caller's code off the worker loop.
        private readonly TaskCompletionSource<TResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<TResult> Completion => _completion.Task;

        public void Cancel(CancellationToken cancelledToken) => _completion.TrySetCanceled(cancelledToken);

        public override async Task ExecuteAsync(CancellationToken disposalToken)
        {
            var cancelledToken = cancellationToken.IsCancellationRequested ? cancellationToken : disposalToken;
            if (cancelledToken.IsCancellationRequested)
            {
                MarkFinished();
                _completion.TrySetCanceled(cancelledToken);
                return;
            }

            try
            {
                TResult result;
                try
                {
                    result = await work(cancellationToken);
                }
                finally
                {
                    // Before the caller has the outcome: a flow of this item that goes on from there, such
                    // as a task that waited for the item, calls in after it and is queued.
                    MarkFinished();
                }

                _completion.TrySetResult(result);
            }
            catch (OperationCanceledException exception)
            {
                _completion.TrySetCanceled(exception.CancellationToken);
            }
            catch (Exception exception)
            {
                _completion.TrySetException(exception);
            }
        }
    }
}
