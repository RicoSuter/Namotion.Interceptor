using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Sources;

public class SubjectSourceBackgroundService : BackgroundService, ISubjectMutationDispatcher
{
    private readonly Lock _lock = new();
    private readonly ISubjectSource _source;
    private readonly IInterceptorSubjectContext _context;
    private readonly ILogger _logger;
    private readonly TimeSpan _bufferTime;
    private readonly TimeSpan _retryTime;

    private List<Action>? _beforeInitializationUpdates = [];
    private ISubjectRegistry? _subjectRegistry;

    private List<SubjectPropertyChange> _changes = [];
    private readonly SemaphoreSlim _writeSemaphore = new(1, 1);

    private List<SubjectPropertyChange> _flushChanges = [];
    private readonly HashSet<PropertyReference> _flushTouchedChanges = [];
    private readonly List<SubjectPropertyChange> _flushDedupedChanges = [];
    private DateTimeOffset _flushLastTime = DateTimeOffset.MinValue;

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
    }

    /// <inheritdoc />
    public void EnqueueSubjectUpdate(Action update)
    {
        if (_beforeInitializationUpdates is not null)
        {
            lock (_lock)
            {
                if (_beforeInitializationUpdates is not null)
                {
                    _beforeInitializationUpdates.Add(update);
                    return;
                }
            }
        }

        try
        {
            _subjectRegistry ??= _context.GetService<ISubjectRegistry>();
            _subjectRegistry.ExecuteSubjectUpdate(update);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to execute subject update.");
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                lock (_lock)
                {
                    _beforeInitializationUpdates = [];
                }

                // Start listening for changes from the source
                using var disposable = await _source.StartListeningAsync(this, stoppingToken).ConfigureAwait(false);
                var applyAction = await _source.LoadCompleteSourceStateAsync(stoppingToken).ConfigureAwait(false);

                lock (_lock)
                {
                    applyAction?.Invoke();

                    // Replay previously buffered updates
                    var beforeInitializationUpdates = _beforeInitializationUpdates;
                    _beforeInitializationUpdates = null;

                    foreach (var action in beforeInitializationUpdates!)
                    {
                        EnqueueSubjectUpdate(action);
                    }
                }
                
                // Subscribe to property changes and sync them to the source
                using var subscription = _context.CreatePropertyChangeQueueSubscription();
                await ProcessPropertyChangesAsync(subscription, stoppingToken);
            }
            catch (Exception ex)
            {
                if (ex is TaskCanceledException or OperationCanceledException)
                {
                    return;
                }

                _logger.LogError(ex, "Failed to listen for changes in source.");
                ResetState();

                await Task.Delay(_retryTime, stoppingToken);
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
            ? Task.Run(async () => await RunPeriodicFlushAsync(periodicTimer, linkedTokenSource.Token), linkedTokenSource.Token)
            : Task.CompletedTask;

        try
        {
            // Ensure we don't block the startup process
            await Task.Yield(); 

            while (subscription.TryDequeue(out var item, linkedTokenSource.Token))
            {
                if (item.Source == _source || !_source.IsPropertyIncluded(item.Property.GetRegisteredProperty()))
                {
                    continue;
                }

                _changes.Add(item);

                if (periodicTimer is null)
                {
                    await WriteToSourceAsync(_changes, linkedTokenSource.Token).ConfigureAwait(false);
                    _changes.Clear();
                }
                else
                {
                    if (item.ChangedTimestamp - _flushLastTime >= _bufferTime)
                    {
                        await TryFlushBufferAsync(item.ChangedTimestamp, linkedTokenSource.Token).ConfigureAwait(false);
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

    private async Task RunPeriodicFlushAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                var now = DateTimeOffset.UtcNow;
                var changes = Volatile.Read(ref _changes);
                if (changes.Count > 0 && now - _flushLastTime >= _bufferTime)
                {
                    await TryFlushBufferAsync(now, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when stopping
        }
    }

    private async ValueTask TryFlushBufferAsync(DateTimeOffset newFlushTime, CancellationToken cancellationToken)
    {
        // Use synchronous try-enter to avoid Task allocation from WaitAsync
        // ReSharper disable once MethodHasAsyncOverload
        if (!_writeSemaphore.Wait(0, cancellationToken))
        {
            return;
        }

        try
        {
            if (_changes.Count == 0)
            {
                return;
            }

            _flushChanges = Interlocked.Exchange(ref _changes, _flushChanges);
            _flushLastTime = newFlushTime;

            _flushTouchedChanges.Clear();
            _flushDedupedChanges.Clear();

            for (var i = _flushChanges.Count - 1; i >= 0; i--)
            {
                var change = _flushChanges[i];
                if (_flushTouchedChanges.Add(change.Property))
                {
                    _flushDedupedChanges.Add(change);
                }
            }

            _flushChanges.Clear();

            if (_flushDedupedChanges.Count > 0)
            {
                await WriteToSourceAsync(_flushDedupedChanges, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _writeSemaphore.Release();
        }
    }

    private async ValueTask WriteToSourceAsync(List<SubjectPropertyChange> changes, CancellationToken cancellationToken)
    {
        try
        {
            await _source.WriteToSourceAsync(changes, cancellationToken);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to write changes to source.");
        }
    }

    public override void Dispose()
    {
        _writeSemaphore.Dispose();
        base.Dispose();
    }

    private void ResetState()
    {
        _changes.Clear();
        _flushChanges.Clear();
        _flushTouchedChanges.Clear();
        _flushDedupedChanges.Clear();
        _flushLastTime = DateTimeOffset.MinValue;
    }
}
