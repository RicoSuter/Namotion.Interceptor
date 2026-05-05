using System.Runtime.CompilerServices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors;

/// <summary>
/// Abstract base for source classes that owns the entire pump lifecycle
/// (buffer -> listen -> load initial state -> run change queue processor -> retry on failure).
/// Derived classes override three protected hooks to plug in protocol-specific behavior.
/// </summary>
/// <remarks>
/// This base implements <see cref="ISubjectSource"/> by bridging its members to the protected hooks,
/// so existing consumers (transaction writer, retry queue, extension methods) continue to work
/// unchanged through the interface surface.
/// </remarks>
public abstract class SubjectSourceBase : BackgroundService, ISubjectSource
{
    private readonly IInterceptorSubjectContext _context;
    private readonly ILogger _logger;
    private readonly TimeSpan _bufferTime;
    private readonly TimeSpan _retryTime;
    private readonly SubjectPropertyWriter _propertyWriter;

    internal WriteRetryQueue? WriteRetryQueue { get; }

    protected SubjectSourceBase(
        IInterceptorSubjectContext context,
        ILogger logger,
        TimeSpan? bufferTime = null,
        TimeSpan? retryTime = null,
        int writeRetryQueueSize = 1000)
    {
        _context = context;
        _logger = logger;
        _bufferTime = bufferTime ?? TimeSpan.FromMilliseconds(8);
        _retryTime = retryTime ?? TimeSpan.FromSeconds(10);

        if (writeRetryQueueSize > 0)
        {
            WriteRetryQueue = new WriteRetryQueue(writeRetryQueueSize, logger);
        }

        _propertyWriter = new SubjectPropertyWriter(this, logger);
    }

    /// <inheritdoc cref="ISubjectConnector.RootSubject" />
    public abstract IInterceptorSubject RootSubject { get; }

    /// <inheritdoc cref="ISubjectSource.WriteBatchSize" />
    public virtual int WriteBatchSize => 0;

    /// <summary>
    /// Initializes the source and starts listening for external changes.
    /// </summary>
    /// <param name="propertyWriter">The writer to use for applying inbound property updates to the subject.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>
    /// An async disposable that can be used to stop listening for changes,
    /// or <c>null</c> if there is nothing to dispose.
    /// </returns>
    protected abstract Task<IAsyncDisposable?> StartListeningAsync(
        SubjectPropertyWriter propertyWriter, CancellationToken cancellationToken);

    /// <summary>
    /// Loads the initial state from the external authoritative system and returns a delegate that applies it.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A delegate that applies the initial state to the subject, or <c>null</c> if there is no state to apply.</returns>
    protected abstract Task<Action?> LoadInitialStateAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Applies a set of property changes to the source.
    /// </summary>
    /// <param name="changes">The changes to apply.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="WriteResult"/> describing which changes succeeded.</returns>
    protected abstract ValueTask<WriteResult> WriteChangesToSourceAsync(
        ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken cancellationToken);

    // --- ISubjectSource bridge ---
    // Existing consumers (SourceTransactionWriter, WriteRetryQueue.FlushAsync, the
    // SubjectSourceExtensions.WriteChangesInBatchesAsync extension) continue to work
    // through the interface. We adapt the protected hooks to the interface signatures.

    async Task<IDisposable?> ISubjectSource.StartListeningAsync(
        SubjectPropertyWriter propertyWriter, CancellationToken cancellationToken)
    {
        var lifetime = await StartListeningAsync(propertyWriter, cancellationToken).ConfigureAwait(false);
        return lifetime is null ? null : new AsyncDisposableAdapter(lifetime);
    }

    Task<Action?> ISubjectSource.LoadInitialStateAsync(CancellationToken cancellationToken)
        => LoadInitialStateAsync(cancellationToken);

    ValueTask<WriteResult> ISubjectSource.WriteChangesAsync(
        ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken cancellationToken)
        => WriteChangesToSourceAsync(changes, cancellationToken);

    int ISubjectSource.WriteBatchSize => WriteBatchSize;

    /// <inheritdoc />
    protected sealed override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _propertyWriter.StartBuffering();
                await using var listenLifetime = await StartListeningAsync(_propertyWriter, stoppingToken).ConfigureAwait(false);

                var applyAction = await LoadInitialStateAsync(stoppingToken).ConfigureAwait(false);
                _propertyWriter.ApplyInitialStateAndResume(applyAction);

                using var processor = new ChangeQueueProcessor(
                    this,
                    _context,
                    propertyReference => propertyReference.TryGetSource(out var source) && source == this,
                    WriteChangesViaRetryQueueAsync,
                    _bufferTime,
                    _logger);

                // Optimistic retry re-apply: after initial state load + ChangeQueueProcessor creation,
                // re-apply queued changes locally if the source hasn't changed the property.
                // ChangeQueueProcessor picks up re-applied changes and sends them to the source as fresh writes.
                ReapplyRetryQueue();

                await processor.ProcessAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to listen for changes in source.");
                try
                {
                    await Task.Delay(_retryTime, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    private async ValueTask WriteChangesViaRetryQueueAsync(ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken cancellationToken)
    {
        // The base type implements ISubjectSource; route through the interface so that the existing
        // SubjectSourceExtensions.WriteChangesInBatchesAsync extension (which handles batching and the
        // per-source semaphore) and WriteRetryQueue.FlushAsync(ISubjectSource, ...) keep working.
        ISubjectSource self = this;

        if (WriteRetryQueue is null)
        {
            // No retry queue - write directly
            try
            {
                var result = await self.WriteChangesInBatchesAsync(changes, cancellationToken).ConfigureAwait(false);
                if (!result.IsFullySuccessful)
                {
                    _logger.LogError(result.Error, "Failed to write {Count} changes to source.",
                        result.FailedChanges.Length);
                }
            }
            catch (OperationCanceledException)
            {
                throw; // Don't swallow cancellation
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Failed to write changes to source.");
            }
            return;
        }

        // First flush any queued changes
        var succeeded = await WriteRetryQueue.FlushAsync(self, cancellationToken).ConfigureAwait(false);
        if (!succeeded)
        {
            WriteRetryQueue.Enqueue(changes);
            return;
        }

        // Write current changes
        try
        {
            var result = await self.WriteChangesInBatchesAsync(changes, cancellationToken).ConfigureAwait(false);
            if (result is { IsFullySuccessful: false, FailedChanges.IsEmpty: false })
            {
                _logger.LogWarning(result.Error, "Failed to write {Count} changes to source, queuing for retry.",
                    result.FailedChanges.Length);
                WriteRetryQueue.Enqueue(result.FailedChanges.ToArray());
            }
        }
        catch (OperationCanceledException)
        {
            throw; // Don't swallow cancellation
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Failed to write {Count} changes to source, queuing for retry.", changes.Length);
            WriteRetryQueue.Enqueue(changes);
        }
    }

    private void ReapplyRetryQueue()
    {
        var retryChanges = WriteRetryQueue?.DrainForLocalReapply();
        if (retryChanges is null || retryChanges.Length == 0)
        {
            return;
        }

        var applied = 0;
        var dropped = 0;
        var failed = 0;
        foreach (var change in retryChanges)
        {
            try
            {
                var property = change.Property;
                var currentValue = property.Metadata.GetValue?.Invoke(property.Subject);
                var oldValue = change.GetOldValue<object>();

                if (Equals(currentValue, oldValue))
                {
                    // Server hasn't changed this property - re-apply client's change locally.
                    // The interceptor chain fires, ChangeQueueProcessor captures the change, and sends it to the source.
                    property.Metadata.SetValue?.Invoke(property.Subject, change.GetNewValue<object>());
                    applied++;
                }
                else
                {
                    dropped++;
                }
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception,
                    "Failed to re-apply retry queue change for property '{PropertyName}', dropping.",
                    change.Property.Name);
                failed++;
            }
        }

        if (dropped > 0 || failed > 0)
        {
            _logger.LogWarning(
                "Retry queue optimistic re-apply: {Applied} re-applied, {Dropped} dropped (source wins), {Failed} failed.",
                applied, dropped, failed);
        }
        else if (applied > 0)
        {
            _logger.LogInformation(
                "Retry queue optimistic re-apply: {Applied} changes re-applied.",
                applied);
        }
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        WriteRetryQueue?.Dispose();
        base.Dispose();
    }

    /// <summary>
    /// Adapts an <see cref="IAsyncDisposable"/> to <see cref="IDisposable"/> for the
    /// <see cref="ISubjectSource.StartListeningAsync"/> bridge. The base's own
    /// <see cref="ExecuteAsync"/> uses <c>await using</c> on the protected hook directly,
    /// so this adapter only fires for external callers that go through the interface.
    /// Sync-over-async via <c>GetAwaiter().GetResult()</c> is safe here because such
    /// callers run inside <see cref="BackgroundService"/> contexts with no captured
    /// <see cref="SynchronizationContext"/>.
    /// </summary>
    private sealed class AsyncDisposableAdapter : IDisposable
    {
        private readonly IAsyncDisposable _inner;

        public AsyncDisposableAdapter(IAsyncDisposable inner) => _inner = inner;

        public void Dispose()
            => _inner.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
