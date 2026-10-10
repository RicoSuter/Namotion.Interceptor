using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace HomeBlaze.Storage.Internal;

/// <summary>
/// Runs work items one at a time, in the order they were handed in. Everything that changes the subject tree
/// or the index of a storage goes through it, which is why neither needs a lock.
/// </summary>
internal sealed class StorageWorker : IDisposable
{
    private readonly Channel<WorkItem> _queue =
        Channel.CreateUnbounded<WorkItem>(new UnboundedChannelOptions { SingleReader = true });

    private readonly CancellationTokenSource _disposalSource = new();
    private readonly AsyncLocal<WorkItem?> _itemOfFlow = new();
    private readonly ILogger? _logger;
    private readonly Task _loop;

    public StorageWorker(ILogger? logger = null)
    {
        _logger = logger;

        // Created within an item of another worker, the loop would otherwise carry the flow of that item into
        // everything it runs.
        using (ExecutionContext.SuppressFlow())
        {
            _loop = Task.Run(ProcessQueueAsync);
        }
    }

    /// <summary>
    /// Runs the work after everything handed in before it. Work handed in from within a running item runs
    /// inline, as part of that item. After disposal the returned task is cancelled, except for inline calls of
    /// the item that is still running, which run as before.
    /// </summary>
    /// <remarks>
    /// A call from any flow that a running item started counts as part of that item for as long as the item or one of its inline calls runs. That includes a flow the item does not await, such as a task or a timer it started. Such a call runs inline, and therefore concurrently with the item unless the item awaits it. Code that runs on the worker must await its calls back into the worker, and must start detached work without the execution flow (<see cref="ExecutionContext.SuppressFlow"/>). The next item starts only after every inline call of the previous item has finished, so an inline call that never completes stalls the worker.
    /// </remarks>
    public Task<TResult> RunAsync<TResult>(Func<CancellationToken, Task<TResult>> work, CancellationToken cancellationToken)
    {
        // Queued, a call from within the running item would wait for the item that waits for it. That the flow
        // carries an item is not enough: a task that an item started keeps the flow of that item after it has
        // finished, and must not run next to a later item.
        var itemOfFlow = _itemOfFlow.Value;
        if (itemOfFlow != null && itemOfFlow.TryEnterInlineCall())
        {
            return RunInlineAsync(itemOfFlow, work, cancellationToken);
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

    // Asynchronous, so that a cancelled token and a delegate that throws synchronously end up in the returned
    // task, as they do for a queued call.
    private static async Task<TResult> RunInlineAsync<TResult>(
        WorkItem item, Func<CancellationToken, Task<TResult>> work, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await work(cancellationToken);
        }
        finally
        {
            item.ExitInlineCall();
        }
    }

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

            // The caller of the item already has its result. Inline calls that the item did not await still
            // belong to it, and the next item must not start next to them.
            await item.CloseWhenInlineCallsFinishedAsync();
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
    /// Does what <see cref="Dispose"/> does and completes when the loop has ended: the running item and its
    /// inline calls have finished. Called from within a running item of this worker, it completes at once.
    /// </summary>
    public Task StopAsync()
    {
        Dispose();

        // The loop ends after the item of this flow, which would wait for itself.
        return _itemOfFlow.Value is { IsClosed: false } ? Task.CompletedTask : _loop;
    }

    private abstract class WorkItem
    {
        private readonly Lock _lock = new();

        private int _pendingInlineCalls;
        private bool _isClosed;
        private TaskCompletionSource? _inlineCallsFinished;

        /// <summary>True once the loop has moved on from the item and its inline calls.</summary>
        public bool IsClosed
        {
            get
            {
                lock (_lock)
                {
                    return _isClosed;
                }
            }
        }

        public abstract Task ExecuteAsync(CancellationToken disposalToken);

        /// <summary>
        /// Registers an inline call that <see cref="ExitInlineCall"/> must end. Returns false once the item is closed.
        /// </summary>
        public bool TryEnterInlineCall()
        {
            // Checked and registered under the lock that closing takes, so that no inline call starts once the
            // loop has moved on.
            lock (_lock)
            {
                if (_isClosed)
                {
                    return false;
                }

                _pendingInlineCalls++;
                return true;
            }
        }

        public void ExitInlineCall()
        {
            lock (_lock)
            {
                if (--_pendingInlineCalls == 0 && _inlineCallsFinished != null)
                {
                    _isClosed = true;
                    _inlineCallsFinished.SetResult();
                }
            }
        }

        /// <summary>
        /// Closes the item as soon as no inline call is pending. The returned task completes when it is closed.
        /// </summary>
        public Task CloseWhenInlineCallsFinishedAsync()
        {
            lock (_lock)
            {
                if (_pendingInlineCalls == 0)
                {
                    _isClosed = true;
                    return Task.CompletedTask;
                }

                // Still open while calls are pending: one of them that calls the worker again would otherwise be
                // queued behind the item it belongs to and wait for itself.
                _inlineCallsFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                return _inlineCallsFinished.Task;
            }
        }
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
                _completion.TrySetCanceled(cancelledToken);
                return;
            }

            try
            {
                _completion.TrySetResult(await work(cancellationToken));
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
