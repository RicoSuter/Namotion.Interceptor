using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors;

/// <summary>
/// Processes property changes from a queue, buffering and deduplicating them before writing.
/// Used by both client sources and server background services.
/// </summary>
public class ChangeQueueProcessor : IDisposable
{
    private const int FlushDedupedBufferMinSize = 256;
    private const int FlushDedupedBufferMaxSize = 1024;

    private readonly IInterceptorSubjectContext _context;
    private readonly Func<RegisteredSubjectProperty, bool> _propertyFilter;
    private readonly Func<ReadOnlyMemory<SubjectPropertyChange>, CancellationToken, ValueTask> _writeHandler;
    private readonly object? _source;
    private readonly ILogger _logger;
    private readonly TimeSpan _bufferTime;

    // Use a concurrent, lock-free queue for collecting changes from the subscription thread.
    private readonly ConcurrentQueue<SubjectPropertyChange> _changes = new();
    private int _flushGate; // 0 = free, 1 = flushing
    private int _disposed; // 0 = not disposed, 1 = disposed (use Interlocked for thread-safe check)

    // Scratch buffers used only while holding the flush gate (single-threaded access)
    private readonly List<SubjectPropertyChange> _flushChanges = [];
    private readonly HashSet<PropertyReference> _flushTouchedChanges = new(PropertyReference.Comparer);

    // Reusable buffer for deduped changes (rented from ArrayPool to avoid allocations on resize)
    private SubjectPropertyChange[] _flushDedupedBuffer = ArrayPool<SubjectPropertyChange>.Shared.Rent(FlushDedupedBufferMinSize);
    private int _flushDedupedCount;

    // Reusable single-item buffer for the no-buffer (immediate) path
    private readonly SubjectPropertyChange[] _immediateBuffer = new SubjectPropertyChange[1];

    /// <summary>
    /// Initializes a new instance of the <see cref="ChangeQueueProcessor"/> class.
    /// </summary>
    /// <param name="source">Source to ignore (to prevent update loops).</param>
    /// <param name="context">The interceptor subject context.</param>
    /// <param name="propertyFilter">Filter to determine if a property should be included.</param>
    /// <param name="writeHandler">Handler to write batched changes.</param>
    /// <param name="bufferTime">Time to buffer changes before flushing.</param>
    /// <param name="logger">The logger.</param>
    public ChangeQueueProcessor(
        object? source,
        IInterceptorSubjectContext context,
        Func<RegisteredSubjectProperty, bool> propertyFilter,
        Func<ReadOnlyMemory<SubjectPropertyChange>, CancellationToken, ValueTask> writeHandler,
        TimeSpan? bufferTime,
        ILogger logger)
    {
        _source = source;
        _context = context;
        _propertyFilter = propertyFilter;
        _writeHandler = writeHandler;
        _logger = logger;
        _bufferTime = bufferTime ?? TimeSpan.FromMilliseconds(8);
    }

    /// <summary>
    /// Processes changes from the queue until cancellation is requested.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The task.</returns>
    public async Task ProcessAsync(CancellationToken cancellationToken)
    {
        using var subscription = _context.CreatePropertyChangeQueueSubscription();

        using var periodicTimer = _bufferTime > TimeSpan.Zero ? new PeriodicTimer(_bufferTime) : null;
        using var linkedTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var flushTask = periodicTimer is not null
            ? Task.Run(async () =>
            {
                try
                {
                    // ReSharper disable AccessToDisposedClosure
                    while (await periodicTimer.WaitForNextTickAsync(linkedTokenSource.Token).ConfigureAwait(false))
                    {
                        await TryFlushAsync(linkedTokenSource.Token).ConfigureAwait(false);
                    }
                    // ReSharper restore AccessToDisposedClosure
                }
                catch (Exception ex)
                {
                    if (ex is not OperationCanceledException)
                    {
                        _logger.LogError(ex, "Failed to flush changes.");
                    }
                }
            }, linkedTokenSource.Token)
            : Task.CompletedTask;

        if (periodicTimer is null)
        {
            _logger.LogWarning(
                "Change queue processor is running without buffering (bufferTime <= 0). " +
                "Each property change will be processed individually without deduplication, " +
                "which can cause high CPU usage under load. " +
                "Consider setting a bufferTime (e.g., 8-50ms) to enable batching and deduplication.");
        }

        try
        {
            await Task.Yield();

            while (subscription.TryDequeue(out var change, linkedTokenSource.Token))
            {
                if (change.Source == _source)
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
                    // Immediate path: send a single change without buffering (zero allocation)
                    _immediateBuffer[0] = change;
                    try
                    {
                        await _writeHandler(_immediateBuffer, linkedTokenSource.Token).ConfigureAwait(false);
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
                    // Buffered path: enqueue lock-free; periodic timer handles flushing
                    _changes.Enqueue(change);
                }
            }
        }
        finally
        {
            try { await linkedTokenSource.CancelAsync().ConfigureAwait(false); } catch { /* ignore */ }
            await flushTask.ConfigureAwait(false);
        }
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    private async ValueTask TryFlushAsync(CancellationToken cancellationToken)
    {
        // Fast, allocation-free try-enter
        if (Interlocked.Exchange(ref _flushGate, 1) == 1)
        {
            return;
        }

        try
        {
            // Drain the concurrent queue into the scratch buffer under exclusive flush
            _flushChanges.Clear();
            while (_changes.TryDequeue(out var change))
            {
                _flushChanges.Add(change);
            }

            if (_flushChanges.Count == 0)
            {
                return;
            }

            _flushTouchedChanges.Clear();
            _flushDedupedCount = 0;

            // Pre-size to avoid resizes under bursts
            _flushTouchedChanges.EnsureCapacity(_flushChanges.Count);

            // Ensure the buffer is large enough (rent from pool to avoid allocations)
            if (_flushDedupedBuffer.Length < _flushChanges.Count)
            {
                ArrayPool<SubjectPropertyChange>.Shared.Return(_flushDedupedBuffer);
                _flushDedupedBuffer = ArrayPool<SubjectPropertyChange>.Shared.Rent(_flushChanges.Count);
            }

            // Deduplicate by Property, keeping the last write, and preserve order of last occurrences
            for (var i = _flushChanges.Count - 1; i >= 0; i--)
            {
                var change = _flushChanges[i];
                if (_flushTouchedChanges.Add(change.Property))
                {
                    _flushDedupedBuffer[_flushDedupedCount++] = change;
                }
            }

            // Reverse in place to keep the ascending order of last occurrences without allocations
            if (_flushDedupedCount > 1)
            {
                Array.Reverse(_flushDedupedBuffer, 0, _flushDedupedCount);
            }

            if (_flushDedupedCount > 0)
            {
                try
                {
                    await _writeHandler(new ReadOnlyMemory<SubjectPropertyChange>(_flushDedupedBuffer, 0, _flushDedupedCount), cancellationToken).ConfigureAwait(false);
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
        finally
        {
            // Clear buffers to allow GC of SubjectPropertyChange objects
            _flushChanges.Clear();
            _flushTouchedChanges.Clear();

            // Clear entire rented array before potential return to pool.
            // SubjectPropertyChange contains object references (Source, boxed values) that must be released.
            Array.Clear(_flushDedupedBuffer, 0, _flushDedupedBuffer.Length);

            if (Volatile.Read(ref _disposed) == 1)
            {
                // Disposed while flushing - return buffer to pool now
                ArrayPool<SubjectPropertyChange>.Shared.Return(_flushDedupedBuffer);
                _flushDedupedBuffer = null!;
            }
            else if (_flushDedupedBuffer.Length >= FlushDedupedBufferMaxSize &&
                     _flushDedupedCount < _flushDedupedBuffer.Length / 4)
            {
                // Shrink buffer if it grew too large (return to pool and rent smaller)
                ArrayPool<SubjectPropertyChange>.Shared.Return(_flushDedupedBuffer);
                _flushDedupedBuffer = ArrayPool<SubjectPropertyChange>.Shared.Rent(FlushDedupedBufferMinSize);
            }

            Volatile.Write(ref _flushGate, 0);
        }
    }

    /// <summary>
    /// Disposes the processor and returns the rented buffer to the pool.
    /// </summary>
    public void Dispose()
    {
        // Atomic check-and-set to prevent double-dispose race condition
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        // Try to acquire gate once - if flush is in progress, it will handle cleanup when it sees _disposed
        if (Interlocked.CompareExchange(ref _flushGate, 1, 0) == 0)
        {
            try
            {
                // Clear and return the buffer to the pool
                Array.Clear(_flushDedupedBuffer, 0, _flushDedupedBuffer.Length);
                ArrayPool<SubjectPropertyChange>.Shared.Return(_flushDedupedBuffer);
                _flushDedupedBuffer = null!;
            }
            finally
            {
                Volatile.Write(ref _flushGate, 0);
            }
        }
    }
}
