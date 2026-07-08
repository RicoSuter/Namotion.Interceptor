using System.Runtime.CompilerServices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors;

/// <summary>
/// Abstract base for source classes that owns the entire pump lifecycle
/// (buffer -> listen -> load initial state -> run change queue processor -> retry on failure).
/// Derived classes override three hooks to plug in protocol-specific behavior:
/// <see cref="StartListeningAsync"/> (protected), <see cref="LoadInitialStateAsync"/> (public),
/// and <see cref="WriteChangesAsync"/> (public).
/// </summary>
public abstract class SubjectSourceBase : BackgroundService, ISubjectSource
{
    private readonly IInterceptorSubjectContext _context;
    private readonly ILogger _logger;
    private readonly TimeSpan _bufferTime;
    private readonly TimeSpan _retryTime;
    private readonly SubjectPropertyWriter _propertyWriter;

    internal WriteRetryQueue? WriteRetryQueue { get; }

    /// <summary>
    /// Gets the number of writes currently queued for retry.
    /// </summary>
    public int PendingWriteCount => WriteRetryQueue?.PendingWriteCount ?? 0;

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

    /// <inheritdoc />
    public abstract Task<Action?> LoadInitialStateAsync(CancellationToken cancellationToken);

    /// <inheritdoc />
    public abstract ValueTask<WriteResult> WriteChangesAsync(
        ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken cancellationToken);

    /// <inheritdoc />
    protected sealed override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _propertyWriter.StartBuffering();
                await using var listenLifetime = await StartListeningAsync(_propertyWriter, stoppingToken).ConfigureAwait(false);

                await _propertyWriter.LoadInitialStateAndResumeAsync(stoppingToken).ConfigureAwait(false);

                using var processor = new ChangeQueueProcessor(
                    this,
                    _context,
                    propertyReference => propertyReference.TryGetSource(out var source) && source == this,
                    WriteChangesViaRetryQueueAsync,
                    _bufferTime,
                    maxQueueDepth: null,
                    logger: _logger);

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
        if (WriteRetryQueue is null)
        {
            // No retry queue - write directly
            try
            {
                var result = await this.WriteChangesInBatchesAsync(changes, cancellationToken).ConfigureAwait(false);
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
        var succeeded = await WriteRetryQueue.FlushAsync(this, cancellationToken).ConfigureAwait(false);
        if (!succeeded)
        {
            WriteRetryQueue.Enqueue(changes);
            return;
        }

        // Write current changes
        try
        {
            var result = await this.WriteChangesInBatchesAsync(changes, cancellationToken).ConfigureAwait(false);
            if (!result.IsFullySuccessful)
            {
                _logger.LogWarning(result.Error, "Failed to write {Count} changes to source, queuing for retry.",
                    result.FailedChanges.Length);
                WriteRetryQueue.Enqueue(result.FailedChanges.AsMemory());
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
}
