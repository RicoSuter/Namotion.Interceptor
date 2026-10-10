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

    private volatile WorkItem? _runningItem;

    public StorageWorker(ILogger? logger = null)
    {
        _logger = logger;
        _ = Task.Run(ProcessQueueAsync);
    }

    /// <summary>
    /// Runs the work after everything handed in before it. Work handed in from within a running item runs
    /// inline. After disposal the returned task is cancelled.
    /// </summary>
    public Task<TResult> RunAsync<TResult>(Func<CancellationToken, Task<TResult>> work, CancellationToken cancellationToken)
    {
        // Queued, a call from within the running item would wait for the item that waits for it. The item is
        // compared, not just a flag: a task that an item started keeps the flow of that item after it has
        // finished, and must not run next to a later item.
        var itemOfFlow = _itemOfFlow.Value;
        if (itemOfFlow != null && ReferenceEquals(itemOfFlow, _runningItem))
        {
            return work(cancellationToken);
        }

        var item = new WorkItem<TResult>(work, cancellationToken);
        if (!_queue.Writer.TryWrite(item))
        {
            item.Cancel();
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
            _runningItem = item;
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
            finally
            {
                _runningItem = null;
            }
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

    private abstract class WorkItem
    {
        public abstract Task ExecuteAsync(CancellationToken disposalToken);
    }

    private sealed class WorkItem<TResult>(
        Func<CancellationToken, Task<TResult>> work,
        CancellationToken cancellationToken) : WorkItem
    {
        // Asynchronous continuations keep the caller's code off the worker loop.
        private readonly TaskCompletionSource<TResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<TResult> Completion => _completion.Task;

        public void Cancel() => _completion.TrySetCanceled();

        public override async Task ExecuteAsync(CancellationToken disposalToken)
        {
            if (cancellationToken.IsCancellationRequested || disposalToken.IsCancellationRequested)
            {
                _completion.TrySetCanceled();
                return;
            }

            try
            {
                _completion.TrySetResult(await work(cancellationToken));
            }
            catch (OperationCanceledException)
            {
                _completion.TrySetCanceled();
            }
            catch (Exception exception)
            {
                _completion.TrySetException(exception);
            }
        }
    }
}
