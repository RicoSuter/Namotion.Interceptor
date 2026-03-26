using System.Runtime.CompilerServices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors;

public class SubjectSourceBackgroundService : BackgroundService
{
    private readonly ISubjectSource _source;
    private readonly IInterceptorSubjectContext _context;
    private readonly ILogger _logger;
    private readonly TimeSpan _bufferTime;
    private readonly TimeSpan _retryTime;
    private readonly SubjectPropertyWriter _propertyWriter;

    internal WriteRetryQueue? WriteRetryQueue { get; }

    public SubjectSourceBackgroundService(
        ISubjectSource source,
        IInterceptorSubjectContext context,
        ILogger logger,
        TimeSpan? bufferTime = null,
        TimeSpan? retryTime = null,
        int writeRetryQueueSize = 1000)
    {
        _source = source;
        _context = context;
        _logger = logger;
        _bufferTime = bufferTime ?? TimeSpan.FromMilliseconds(8);
        _retryTime = retryTime ?? TimeSpan.FromSeconds(10);

        if (writeRetryQueueSize > 0)
        {
            WriteRetryQueue = new WriteRetryQueue(writeRetryQueueSize, logger);
        }

        _propertyWriter = new SubjectPropertyWriter(source, logger);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _propertyWriter.StartBuffering();
                var disposable = await _source.StartListeningAsync(_propertyWriter, stoppingToken).ConfigureAwait(false);
                try
                {
                    await _propertyWriter.LoadInitialStateAndResumeAsync(stoppingToken);

                    using var processor = new ChangeQueueProcessor(
                        _source,
                        _context,
                        propertyReference => propertyReference.TryGetSource(out var source) && source == _source,
                        WriteChangesAsync,
                        _bufferTime,
                        _logger);

                    // Optimistic retry re-apply: after initial state load + ChangeQueueProcessor creation,
                    // re-apply queued changes locally if the source hasn't changed the property.
                    // ChangeQueueProcessor picks up re-applied changes and sends them to the source as fresh writes.
                    ReapplyRetryQueue();

                    await processor.ProcessAsync(stoppingToken).ConfigureAwait(false);
                }
                finally
                {
                    if (disposable is IAsyncDisposable asyncDisposable)
                    {
                        await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                    }
                    else
                    {
                        disposable?.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                if (ex is TaskCanceledException or OperationCanceledException)
                {
                    return;
                }

                _logger.LogError(ex, "Failed to listen for changes in source.");
                await Task.Delay(_retryTime, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    private async ValueTask WriteChangesAsync(ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken cancellationToken)
    {
        if (WriteRetryQueue is null)
        {
            // No retry queue - write directly
            try
            {
                var result = await _source.WriteChangesInBatchesAsync(changes, cancellationToken).ConfigureAwait(false);
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
        var succeeded = await WriteRetryQueue.FlushAsync(_source, cancellationToken).ConfigureAwait(false);
        if (!succeeded)
        {
            WriteRetryQueue.Enqueue(changes);
            return;
        }

        // Write current changes
        try
        {
            var result = await _source.WriteChangesInBatchesAsync(changes, cancellationToken).ConfigureAwait(false);
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
        foreach (var change in retryChanges)
        {
            var property = change.Property;
            var currentValue = property.Metadata.GetValue?.Invoke(property.Subject);
            var oldValue = change.GetOldValue<object>();

            if (Equals(currentValue, oldValue))
            {
                // Server hasn't changed this property — re-apply client's change locally.
                // The interceptor chain fires, ChangeQueueProcessor captures the change, and sends it to the source.
                property.Metadata.SetValue?.Invoke(property.Subject, change.GetNewValue<object>());
                applied++;
            }
            else
            {
                dropped++;
            }
        }

        if (dropped > 0)
        {
            _logger.LogWarning(
                "Retry queue optimistic re-apply: {Applied} re-applied, {Dropped} dropped (source wins).",
                applied, dropped);
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
