using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Sources;

/// <summary>
/// Processes property changes from a queue, buffering and deduplicating them before writing.
/// Used by both client sources and server background services.
/// </summary>
public class ChangeQueueProcessor
{
    private readonly IInterceptorSubjectContext _context;
    private readonly Func<RegisteredSubjectProperty, bool> _propertyFilter;
    private readonly Func<ReadOnlyMemory<SubjectPropertyChange>, CancellationToken, ValueTask> _writeHandler;
    private readonly object? _sourceToIgnore;
    private readonly ILogger _logger;
    private readonly TimeSpan _bufferTime;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChangeQueueProcessor"/> class.
    /// </summary>
    /// <param name="context">The interceptor subject context.</param>
    /// <param name="propertyFilter">Filter to determine if a property should be included.</param>
    /// <param name="writeHandler">Handler to write batched changes.</param>
    /// <param name="sourceToIgnore">Source to ignore (to prevent update loops).</param>
    /// <param name="logger">The logger.</param>
    /// <param name="bufferTime">Time to buffer changes before flushing.</param>
    public ChangeQueueProcessor(
        IInterceptorSubjectContext context,
        Func<RegisteredSubjectProperty, bool> propertyFilter,
        Func<ReadOnlyMemory<SubjectPropertyChange>, CancellationToken, ValueTask> writeHandler,
        object? sourceToIgnore,
        ILogger logger,
        TimeSpan? bufferTime = null)
    {
        _context = context;
        _propertyFilter = propertyFilter;
        _writeHandler = writeHandler;
        _sourceToIgnore = sourceToIgnore;
        _logger = logger;
        _bufferTime = bufferTime ?? TimeSpan.FromMilliseconds(100);
    }

    /// <summary>
    /// Processes changes from the queue until cancellation is requested.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The task.</returns>
    public async Task ProcessAsync(CancellationToken cancellationToken)
    {
        using var subscription = _context.CreatePropertyChangeQueueSubscription();
        var changes = new Dictionary<PropertyReference, SubjectPropertyChange>();

        using var periodicTimer = _bufferTime > TimeSpan.Zero ? new PeriodicTimer(_bufferTime) : null;
        using var linkedTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var flushTask = periodicTimer is not null
            ? Task.Run(async () =>
            {
                try
                {
                    while (await periodicTimer.WaitForNextTickAsync(linkedTokenSource.Token).ConfigureAwait(false))
                    {
                        await TryFlushAsync(changes, linkedTokenSource.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Expected when stopping
                }
            }, linkedTokenSource.Token)
            : Task.CompletedTask;

        try
        {
            await Task.Yield();

            while (subscription.TryDequeue(out var change, linkedTokenSource.Token))
            {
                if (change.Source == _sourceToIgnore)
                {
                    continue;
                }

                var property = change.Property.TryGetRegisteredProperty();
                if (property is null || !_propertyFilter(property))
                {
                    continue;
                }

                if (periodicTimer is null)
                {
                    // Immediate path: send single change without buffering
                    try
                    {
                        await _writeHandler(new[] { change }, linkedTokenSource.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to write changes.");
                    }
                }
                else
                {
                    // Buffered path: deduplicate by keeping only latest change per property
                    lock (changes)
                    {
                        changes[change.Property] = change;
                    }
                }
            }
        }
        finally
        {
            try { await linkedTokenSource.CancelAsync().ConfigureAwait(false); } catch { /* ignore */ }
            await flushTask.ConfigureAwait(false);
        }
    }

    private async Task TryFlushAsync(Dictionary<PropertyReference, SubjectPropertyChange> changes, CancellationToken cancellationToken)
    {
        SubjectPropertyChange[] changesArray;
        lock (changes)
        {
            if (changes.Count == 0)
            {
                return;
            }
            changesArray = changes.Values.ToArray();
            changes.Clear();
        }

        try
        {
            await _writeHandler(changesArray, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write changes.");
        }
    }
}
