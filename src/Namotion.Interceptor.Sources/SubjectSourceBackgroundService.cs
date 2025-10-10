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
                await Task.Delay(_retryTime, stoppingToken);
            }
        }
    }

    private async Task ProcessPropertyChangesAsync(
        ChannelReader<SubjectPropertyChange> reader,
        CancellationToken stoppingToken)
    {
        var state = new ProcessingState
        {
            Buffer = [],
            DedupSet = [],
            DedupedBuffer = [],
            WriteSemaphore = new SemaphoreSlim(1, 1),
            LastFlushTime = DateTimeOffset.UtcNow
        };

        using var periodicTimer = new PeriodicTimer(_bufferTime);
        using var linkedTokenSource = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

        var flushTask = RunPeriodicFlushAsync(periodicTimer, state, linkedTokenSource.Token);
        try
        {
            await foreach (var item in reader.ReadAllAsync(linkedTokenSource.Token))
            {
                if (item.Source == _source || !_source.IsPropertyIncluded(item.Property.GetRegisteredProperty()))
                {
                    continue;
                }

                state.Buffer.Add(item);

                if (item.Timestamp - state.LastFlushTime >= _bufferTime)
                {
                    await TryFlushBufferAsync(state, item.Timestamp, linkedTokenSource.Token);
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

            await state.WriteSemaphore.WaitAsync(CancellationToken.None);
            try
            {
                if (state.Buffer.Count > 0)
                {
                    DeduplicateBuffer(state.Buffer, state.DedupSet, state.DedupedBuffer);

                    if (state.DedupedBuffer.Count > 0)
                    {
                        await WriteToSourceAsync(state.DedupedBuffer, CancellationToken.None);
                    }
                }
            }
            finally
            {
                state.WriteSemaphore.Release();
                state.WriteSemaphore.Dispose();
            }
        }
    }

    private async Task RunPeriodicFlushAsync(PeriodicTimer timer, ProcessingState state, CancellationToken cancellationToken)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                var now = DateTimeOffset.UtcNow;
                
                if (state.Buffer.Count > 0 && now - state.LastFlushTime >= _bufferTime)
                {
                    await TryFlushBufferAsync(state, now, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when stopping
        }
    }

    private ValueTask TryFlushBufferAsync(ProcessingState state, DateTimeOffset newFlushTime, CancellationToken cancellationToken)
    {
        if (!state.WriteSemaphore.Wait(0, cancellationToken))
        {
            return ValueTask.CompletedTask;
        }

        return FlushBufferCoreAsync(state, newFlushTime, cancellationToken);
    }

    private async ValueTask FlushBufferCoreAsync(ProcessingState state, DateTimeOffset newFlushTime, CancellationToken cancellationToken)
    {
        try
        {
            if (state.Buffer.Count == 0)
            {
                return;
            }

            DeduplicateBuffer(state.Buffer, state.DedupSet, state.DedupedBuffer);
            state.LastFlushTime = newFlushTime;

            if (state.DedupedBuffer.Count > 0)
            {
                await WriteToSourceAsync(state.DedupedBuffer, cancellationToken);
            }
        }
        finally
        {
            state.WriteSemaphore.Release();
        }
    }

    private static void DeduplicateBuffer(List<SubjectPropertyChange> buffer, HashSet<PropertyReference> dedupSet, List<SubjectPropertyChange> dedupedBuffer)
    {
        dedupSet.Clear();
        dedupedBuffer.Clear();

        for (var i = buffer.Count - 1; i >= 0; i--)
        {
            var change = buffer[i];
            if (dedupSet.Add(change.Property))
            {
                dedupedBuffer.Add(change);
            }
        }

        buffer.Clear();
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

    private sealed class ProcessingState
    {
        public required List<SubjectPropertyChange> Buffer { get; init; }

        public required HashSet<PropertyReference> DedupSet { get; init; }

        public required List<SubjectPropertyChange> DedupedBuffer { get; init; }

        public required SemaphoreSlim WriteSemaphore { get; init; }

        public DateTimeOffset LastFlushTime { get; set; }
    }
}