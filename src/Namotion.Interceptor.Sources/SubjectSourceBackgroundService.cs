using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Namotion.Interceptor.Sources;

public class SubjectSourceBackgroundService : BackgroundService
{
    private readonly ISubjectSource _source;
    private readonly IInterceptorSubjectContext _context;
    private readonly ILogger _logger;
    private readonly TimeSpan _bufferTime;
    private readonly TimeSpan _retryTime;
    
    private readonly SubjectUpdater _updater;

    // Use a concurrent, lock-free queue for collecting changes from the subscription thread.
    private readonly ConcurrentQueue<SubjectPropertyChange> _changes = new();
    private int _flushGate = 0; // 0 = free, 1 = flushing

    // Scratch buffers used only while holding the write semaphore (single-threaded access)
    private readonly List<SubjectPropertyChange> _flushChanges = [];
    private readonly HashSet<PropertyReference> _flushTouchedChanges = new(PropertyReference.Comparer);
    private readonly List<SubjectPropertyChange> _flushDedupedChanges = [];

    // Reusable single-item buffer for the no-buffer (immediate) path
    private readonly List<SubjectPropertyChange> _immediateChanges = new(1);

    // Use ticks to avoid torn reads of DateTimeOffset across threads
    private long _flushLastTicks = 0L;

    public SubjectSourceBackgroundService(
        ISubjectSource source,
        IInterceptorSubjectContext context,
        ILogger logger,
        TimeSpan? bufferTime = null,
        TimeSpan? retryTime = null)
    {
        _source = source;
        _context = context;
        _logger = logger;
        _bufferTime = bufferTime ?? TimeSpan.FromMilliseconds(8);
        _retryTime = retryTime ?? TimeSpan.FromSeconds(10);
        _updater = new SubjectUpdater(source, logger);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _updater.StartCollectingUpdates();
                var disposable = await _source.StartListeningAsync(_updater, stoppingToken).ConfigureAwait(false);
                try
                {
                    await _updater.LoadCompleteStateAndReplayUpdatesAsync(stoppingToken);

                    using var subscription = _context.CreatePropertyChangeQueueSubscription();
                    await ProcessPropertyChangesAsync(subscription, stoppingToken).ConfigureAwait(false);
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
                // ResetState is called AFTER disposal in the finally block above,
                // so all resources from the previous attempt are already cleaned up.
                ResetState();

                await Task.Delay(_retryTime, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task ProcessPropertyChangesAsync(PropertyChangeQueueSubscription subscription, CancellationToken stoppingToken)
    {
        ResetState();

        using var periodicTimer = _bufferTime > TimeSpan.Zero ? new PeriodicTimer(_bufferTime) : null;
        using var linkedTokenSource = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

        var flushTask = periodicTimer is not null
            // ReSharper disable AccessToDisposedClosure
            ? Task.Run(async () => await RunPeriodicFlushAsync(
                periodicTimer, linkedTokenSource.Token).ConfigureAwait(false), linkedTokenSource.Token)
            : Task.CompletedTask;

        try
        {
            // Ensure we don't block the startup process
            await Task.Yield(); 

            while (subscription.TryDequeue(out var item, linkedTokenSource.Token))
            {
                if (item.Source == _source)
                {
                    continue; // Ignore changes originating from this source (avoid update loops)
                }
                
                var registeredProperty = item.Property.TryGetRegisteredProperty();
                if (registeredProperty is null || !_source.IsPropertyIncluded(registeredProperty))
                {
                    // Property is null when subject has already been attached (ignore change)
                    continue;
                }

                if (periodicTimer is null)
                {
                    // Immediate path: send the single change without buffering using a reusable list (no allocations)
                    _immediateChanges.Add(item);
                    await WriteToSourceAsync(_immediateChanges, linkedTokenSource.Token).ConfigureAwait(false);
                    _immediateChanges.Clear();
                }
                else
                {
                    // Buffered path: enqueue lock-free; periodic timer handles flushing
                    _changes.Enqueue(item);
                    
                    // Flush directly when needed (currently disabled in favor of periodic flush only)
                    // var lastTicks = Volatile.Read(ref _flushLastTicks);
                    // if (item.ChangedTimestamp.UtcTicks - lastTicks >= _bufferTime.Ticks)
                    // {
                    //     await TryFlushBufferAsync(item.ChangedTimestamp.UtcTicks, linkedTokenSource.Token).ConfigureAwait(false);
                    // }
                }
            }
        }
        finally
        {
            try { await linkedTokenSource.CancelAsync().ConfigureAwait(false); } catch { /* ignore */ }
            await flushTask.ConfigureAwait(false);
        }
    }

    private async Task RunPeriodicFlushAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                var nowTicks = DateTimeOffset.UtcNow.UtcTicks;
                var lastTicks = Volatile.Read(ref _flushLastTicks);
                if (nowTicks - lastTicks >= _bufferTime.Ticks)
                {
                    await TryFlushBufferAsync(nowTicks, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when stopping
        }
    }

    private async ValueTask TryFlushBufferAsync(long newFlushTicks, CancellationToken cancellationToken)
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

            Volatile.Write(ref _flushLastTicks, newFlushTicks);

            _flushTouchedChanges.Clear();
            _flushDedupedChanges.Clear();

            // Pre-size to avoid resizes under bursts
            _flushTouchedChanges.EnsureCapacity(_flushChanges.Count);
            _flushDedupedChanges.EnsureCapacity(_flushChanges.Count);

            // Deduplicate by Property, keeping the last write, and preserve order of last occurrences
            for (var i = _flushChanges.Count - 1; i >= 0; i--)
            {
                var change = _flushChanges[i];
                if (_flushTouchedChanges.Add(change.Property))
                {
                    _flushDedupedChanges.Add(change);
                }
            }

            // Reverse in place to keep ascending order of last occurrences without allocations
            if (_flushDedupedChanges.Count > 1)
            {
                _flushDedupedChanges.Reverse();
            }
            
            if (_flushDedupedChanges.Count > 0)
            {
                await WriteToSourceAsync(_flushDedupedChanges, cancellationToken).ConfigureAwait(false);
            }

            _flushChanges.Clear();
        }
        finally
        {
            Volatile.Write(ref _flushGate, 0);
        }
    }

    private async ValueTask WriteToSourceAsync(IReadOnlyList<SubjectPropertyChange> changes, CancellationToken cancellationToken)
    {
        try
        {
            await _source.WriteToSourceAsync(changes, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to write changes to source.");
        }
    }

    private void ResetState()
    {
        _changes.Clear();
        _flushChanges.Clear();
        _flushTouchedChanges.Clear();
        _flushDedupedChanges.Clear();
        Volatile.Write(ref _flushLastTicks, 0L);
        Volatile.Write(ref _flushGate, 0);
    }
}
