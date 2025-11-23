using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Sources;

public class SubjectSourceBackgroundService : BackgroundService
{
    private readonly ISubjectSource _source;
    private readonly IInterceptorSubjectContext _context;
    private readonly ILogger _logger;
    private readonly TimeSpan _bufferTime;
    private readonly TimeSpan _retryTime;
    private readonly WriteRetryQueue? _writeRetryQueue;
    private readonly SubjectPropertyWriter _propertyWriter;

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
            _writeRetryQueue = new WriteRetryQueue(writeRetryQueueSize, logger);
        }

        _propertyWriter = new SubjectPropertyWriter(
            source,
            _writeRetryQueue is not null ? ct => _writeRetryQueue.FlushAsync(source, ct) : null,
            logger);
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
                    await _propertyWriter.CompleteInitializationAsync(stoppingToken);

                    var processor = new ChangeQueueProcessor(
                        _source,
                        _context,
                        prop => _source.IsPropertyIncluded(prop),
                        WriteChangesAsync,
                        _bufferTime,
                        _logger);

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

    protected async ValueTask WriteChangesAsync(ReadOnlyMemory<SubjectPropertyChange> changes, CancellationToken cancellationToken)
    {
        if (_writeRetryQueue is null)
        {
            // No retry queue - write directly
            try
            {
                await _source.WriteChangesInBatchesAsync(changes, cancellationToken).ConfigureAwait(false);
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
        var succeeded = await _writeRetryQueue.FlushAsync(_source, cancellationToken).ConfigureAwait(false);
        if (!succeeded)
        {
            _writeRetryQueue.Enqueue(changes);
            return;
        }

        // Write current changes
        try
        {
            await _source.WriteChangesInBatchesAsync(changes, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw; // Don't swallow cancellation
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Failed to write {Count} changes to source, queuing for retry.", changes.Length);
            _writeRetryQueue.Enqueue(changes);
        }
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        _writeRetryQueue?.Dispose();
        base.Dispose();
    }
}
