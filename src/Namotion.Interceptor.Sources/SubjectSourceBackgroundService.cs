using System.Threading.Channels;
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
    
    private readonly List<SubjectPropertyChange> _buffer = [];
    private readonly HashSet<PropertyReference> _dedupSet = [];
    private readonly List<SubjectPropertyChange> _dedupedBuffer = [];
    private readonly SemaphoreSlim _writeSemaphore = new(1, 1);
    private DateTimeOffset _lastFlushTime = DateTimeOffset.MinValue;

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
                using var disposable = await _source.StartListeningAsync(this, stoppingToken);
                var applyAction = await _source.LoadCompleteSourceStateAsync(stoppingToken);
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
                using var subscription = _context.CreatePropertyChangedChannelSubscription();
                await ProcessPropertyChangesAsync(subscription.Reader, stoppingToken);
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

    private async Task ProcessPropertyChangesAsync(ChannelReader<SubjectPropertyChange> reader, CancellationToken stoppingToken)
    {
        ResetState();

        using var periodicTimer = new PeriodicTimer(_bufferTime);
        using var linkedTokenSource = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

        var flushTask = RunPeriodicFlushAsync(periodicTimer, linkedTokenSource.Token);
        try
        {
            await foreach (var item in reader.ReadAllAsync(linkedTokenSource.Token))
            {
                if (item.Source == _source || !_source.IsPropertyIncluded(item.Property.GetRegisteredProperty()))
                {
                    continue;
                }

                _buffer.Add(item);
                if (item.Timestamp - _lastFlushTime >= _bufferTime)
                {
                    await TryFlushBufferAsync(item.Timestamp, linkedTokenSource.Token);
                }
            }
        }
        finally
        {
            await linkedTokenSource.CancelAsync();
            try
            {
                await flushTask;
            }
            catch (OperationCanceledException)
            {
                // Expected
            }

            if (_buffer.Count > 0)
            {
                DeduplicateBuffer();
                if (_dedupedBuffer.Count > 0)
                {
                    await WriteToSourceAsync(_dedupedBuffer, CancellationToken.None);
                }
            }
        }
    }

    private async Task RunPeriodicFlushAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                var now = DateTimeOffset.UtcNow;
                if (_buffer.Count > 0 && now - _lastFlushTime >= _bufferTime)
                {
                    await TryFlushBufferAsync(now, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when stopping
        }
    }

    private ValueTask TryFlushBufferAsync(DateTimeOffset newFlushTime, CancellationToken cancellationToken)
    {
        if (!_writeSemaphore.Wait(0, cancellationToken))
        {
            return ValueTask.CompletedTask;
        }

        return FlushBufferCoreAsync(newFlushTime, cancellationToken);
    }

    private async ValueTask FlushBufferCoreAsync(DateTimeOffset newFlushTime, CancellationToken cancellationToken)
    {
        try
        {
            if (_buffer.Count == 0)
            {
                return;
            }

            DeduplicateBuffer();
            _lastFlushTime = newFlushTime;

            if (_dedupedBuffer.Count > 0)
            {
                await WriteToSourceAsync(_dedupedBuffer, cancellationToken);
            }
        }
        finally
        {
            _writeSemaphore.Release();
        }
    }

    private void DeduplicateBuffer()
    {
        _dedupSet.Clear();
        _dedupedBuffer.Clear();

        for (var i = _buffer.Count - 1; i >= 0; i--)
        {
            var change = _buffer[i];
            if (_dedupSet.Add(change.Property))
            {
                _dedupedBuffer.Add(change);
            }
        }

        _buffer.Clear();
    }

    private async Task WriteToSourceAsync(List<SubjectPropertyChange> changes, CancellationToken cancellationToken)
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
        _buffer.Clear();
        _dedupSet.Clear();
        _dedupedBuffer.Clear();
        _lastFlushTime = DateTimeOffset.MinValue;
    }
}