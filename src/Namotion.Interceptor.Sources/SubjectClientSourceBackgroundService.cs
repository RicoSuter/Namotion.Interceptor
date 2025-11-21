using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Sources;

public class SubjectClientSourceBackgroundService : SubjectSourceBackgroundService
{
    private readonly ISubjectSource _source;
    private readonly WriteRetryQueue? _writeRetryQueue;
    private readonly ILogger _logger;
    
    public SubjectClientSourceBackgroundService(
        ISubjectClientSource source,
        IInterceptorSubjectContext context,
        ILogger logger,
        TimeSpan? bufferTime = null,
        TimeSpan? retryTime = null,
        int? writeRetryQueueSize = null)
        : base(source, context, logger, bufferTime, retryTime)
    {
        _source = source;
        _logger = logger;

        if (writeRetryQueueSize is > 0)
        {
            _writeRetryQueue = new WriteRetryQueue(writeRetryQueueSize.Value, logger);
        }

        UpdateBuffer = new SourceUpdateBuffer(source, _writeRetryQueue is not null ? ct => _writeRetryQueue.FlushAsync(source, ct) : null, logger);
    }
    
    protected override async ValueTask WriteToSourceAsync(ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken cancellationToken)
    {
        if (_writeRetryQueue is null)
        {
            await base.WriteToSourceAsync(changes, cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            var succeeded = await _writeRetryQueue.FlushAsync(_source, cancellationToken).ConfigureAwait(false);
            if (succeeded)
            {
                await _source.WriteToSourceInBatchesAsync(changes, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                _writeRetryQueue.EnqueueBatch(changes);
            }
        }
        catch (OperationCanceledException)
        {
            throw; // Don't swallow cancellation
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Failed to write {Count} changes to source, queuing for retry.", changes.Length);
            _writeRetryQueue.EnqueueBatch(changes);
        }
    }
}
