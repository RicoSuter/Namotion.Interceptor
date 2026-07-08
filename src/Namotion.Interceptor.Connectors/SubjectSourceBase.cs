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
                // Create the processor (and its change queue subscription) before any
                // model-visible effect of the connection, so application writes made
                // while connecting are captured instead of silently lost. Draining only
                // starts with ProcessAsync below, after the initial state is applied:
                // the ownership filter then sees the claims established during the load,
                // and captured writes the load overwrote are dropped as superseded.
                using var processor = new ChangeQueueProcessor(
                    this,
                    _context,
                    propertyReference => propertyReference.TryGetSource(out var source) && source == this,
                    WriteChangesViaRetryQueueAsync,
                    _bufferTime,
                    maxQueueDepth: null,
                    logger: _logger);

                _propertyWriter.StartBuffering();
                await using var listenLifetime = await StartListeningAsync(_propertyWriter, stoppingToken).ConfigureAwait(false);

                await _propertyWriter.LoadInitialStateAndResumeAsync(stoppingToken).ConfigureAwait(false);

                // Single reconcile point after initial state load: restore (source unchanged),
                // send (already current), or drop (source diverged). See ReconcileRetryQueueAsync.
                await ReconcileRetryQueueAsync(stoppingToken).ConfigureAwait(false);

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

    private async Task ReconcileRetryQueueAsync(CancellationToken cancellationToken)
    {
        var retryChanges = WriteRetryQueue?.DrainForLocalReapply();
        if (retryChanges is null || retryChanges.Length == 0)
        {
            return;
        }

        var restored = 0;
        var sent = 0;
        var dropped = 0;
        var failed = 0;
        List<SubjectPropertyChange>? toSend = null;

        foreach (var change in retryChanges)
        {
            try
            {
                var property = change.Property;
                var currentValue = property.Metadata.GetValue?.Invoke(property.Subject);

                if (Equals(currentValue, change.GetNewValue<object?>()))
                {
                    // Already the current model value: the source has not received it, so send it.
                    (toSend ??= []).Add(change);
                    sent++;
                }
                else if (Equals(currentValue, change.GetOldValue<object?>()))
                {
                    // Source still at the baseline the write was based on: restore locally. The
                    // connected phase captures and sends the re-applied write.
                    property.Metadata.SetValue?.Invoke(property.Subject, change.GetNewValue<object>());
                    restored++;
                }
                else
                {
                    // Source diverged from the baseline: source wins.
                    dropped++;
                }
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception,
                    "Failed to reconcile retry queue change for property '{PropertyName}', dropping.",
                    change.Property.Name);
                failed++;
            }
        }

        if (toSend is not null)
        {
            WriteRetryQueue!.Enqueue(toSend.ToArray());
            await WriteRetryQueue.FlushAsync(this, cancellationToken).ConfigureAwait(false);
        }

        if (dropped > 0 || failed > 0)
        {
            _logger.LogWarning(
                "Retry queue reconcile: {Restored} restored, {Sent} sent, {Dropped} dropped (source wins), {Failed} failed.",
                restored, sent, dropped, failed);
        }
        else if (restored > 0 || sent > 0)
        {
            _logger.LogInformation(
                "Retry queue reconcile: {Restored} restored, {Sent} sent.", restored, sent);
        }
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        WriteRetryQueue?.Dispose();
        base.Dispose();
    }
}
