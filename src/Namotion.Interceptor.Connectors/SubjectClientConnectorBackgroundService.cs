using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors;

public class SubjectClientConnectorBackgroundService : SubjectConnectorBackgroundService
{
    private readonly ISubjectConnector _connector;
    private readonly WriteRetryQueue? _writeRetryQueue;
    private readonly ILogger _logger;

    public SubjectClientConnectorBackgroundService(
        ISubjectClientConnector connector,
        IInterceptorSubjectContext context,
        ILogger logger,
        TimeSpan? bufferTime = null,
        TimeSpan? retryTime = null,
        int? writeRetryQueueSize = null)
        : base(connector, context, logger, bufferTime, retryTime)
    {
        _connector = connector;
        _logger = logger;

        if (writeRetryQueueSize is > 0)
        {
            _writeRetryQueue = new WriteRetryQueue(writeRetryQueueSize.Value, logger);
        }

        PropertyWriter = new SubjectPropertyWriter(connector, _writeRetryQueue is not null ? ct => _writeRetryQueue.FlushAsync(connector, ct) : null, logger);
    }

    protected override async ValueTask WriteToSourceAsync(ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken cancellationToken)
    {
        if (_writeRetryQueue is null)
        {
            await base.WriteToSourceAsync(changes, cancellationToken).ConfigureAwait(false);
            return;
        }

        var succeeded = await _writeRetryQueue.FlushAsync(_connector, cancellationToken).ConfigureAwait(false);
        if (!succeeded)
        {
            _writeRetryQueue.Enqueue(changes);
            return;
        }

        try
        {
            await _connector.WriteChangesInBatchesAsync(changes, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw; // Don't swallow cancellation
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Failed to write {Count} changes to connector, queuing for retry.", changes.Length);
            _writeRetryQueue.Enqueue(changes);
        }
    }
}
