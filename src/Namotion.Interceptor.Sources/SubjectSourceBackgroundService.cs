using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Sources;

public class SubjectSourceBackgroundService : BackgroundService, ISubjectMutationDispatcher
{
    private readonly ISubjectSource _source;
    private readonly IInterceptorSubjectContext _context;
    private readonly ILogger _logger;
    private readonly TimeSpan _bufferTime;
    private readonly TimeSpan _retryTime;

    private List<Action>? _beforeInitializationUpdates = [];

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
        lock (this)
        {
            if (_beforeInitializationUpdates is not null)
            {
                _beforeInitializationUpdates.Add(update);
            }
            else
            {
                try
                {
                    var registry = _context.GetService<ISubjectRegistry>();
                    registry.ExecuteSubjectUpdate(update);
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Failed to execute subject update.");
                }
            }
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                lock (this)
                {
                    _beforeInitializationUpdates = [];
                }

                // start listening for changes
                using var disposable = await _source.StartListeningAsync(this, stoppingToken);
                
                // read complete data set from source
                var applyAction = await _source.LoadCompleteSourceStateAsync(stoppingToken);
                lock (this)
                {
                    applyAction?.Invoke();

                    // replaying previously buffered updates
                    var beforeInitializationUpdates = _beforeInitializationUpdates;
                    _beforeInitializationUpdates = null;
                    
                    foreach (var action in beforeInitializationUpdates!)
                    {
                        EnqueueSubjectUpdate(action);
                    }
                }

                var subscription = _context.CreatePropertyChangedChannelSubscription();
                
                var buffer = new List<SubjectPropertyChange>();
                var dedupSet = new HashSet<PropertyReference>();
                var dedupedBuffer = new List<SubjectPropertyChange>();
                var lockObj = new object();
                DateTimeOffset lastFlushTime = DateTimeOffset.UtcNow;
                var hasPendingWrite = false;
                
                using var periodicTimer = new PeriodicTimer(_bufferTime);
                
                // Start the periodic flush task
                var flushTask = Task.Run(async () =>
                {
                    try
                    {
                        while (await periodicTimer.WaitForNextTickAsync(stoppingToken))
                        {
                            bool shouldWrite = false;
                            
                            lock (lockObj)
                            {
                                if (buffer.Count > 0)
                                {
                                    var now = DateTimeOffset.UtcNow;
                                    if (now - lastFlushTime >= _bufferTime)
                                    {
                                        // deduplicate - reuse dedupedBuffer, no allocations
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
                                        lastFlushTime = now;
                                        
                                        if (dedupedBuffer.Count > 0)
                                        {
                                            shouldWrite = true;
                                            hasPendingWrite = true;
                                        }
                                    }
                                }
                            }
                            
                            // write outside of lock - dedupedBuffer is not modified until next lock
                            if (shouldWrite)
                            {
                                try
                                {
                                    await _source.WriteToSourceAsync(dedupedBuffer, stoppingToken);
                                }
                                catch (Exception e)
                                {
                                    _logger.LogError(e, "Failed to write changes to source (timer flush).");
                                }
                                finally
                                {
                                    lock (lockObj)
                                    {
                                        hasPendingWrite = false;
                                    }
                                }
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // Expected when stopping
                    }
                }, stoppingToken);
                
                try
                {
                    await foreach (var item in subscription.ReadAllAsync(stoppingToken))
                    {
                        // filter: ignore changes from the source itself and only include properties that are part of the source
                        if (item.Source == _source || !_source.IsPropertyIncluded(item.Property.GetRegisteredProperty()))
                        {
                            continue;
                        }

                        bool shouldWrite = false;
                        
                        lock (lockObj)
                        {
                            buffer.Add(item);

                            // check if buffer time has elapsed based on item timestamp and no pending write
                            if (!hasPendingWrite && item.Timestamp - lastFlushTime >= _bufferTime)
                            {
                                // deduplicate: keep only the latest change per property (reverse order, then distinct)
                                // iterate backwards through buffer and only add the first occurrence of each property
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
                                lastFlushTime = item.Timestamp;
                                
                                if (dedupedBuffer.Count > 0)
                                {
                                    shouldWrite = true;
                                    hasPendingWrite = true;
                                }
                            }
                        }
                        
                        // write outside of lock - dedupedBuffer is not modified until next lock
                        if (shouldWrite)
                        {
                            try
                            {
                                await _source.WriteToSourceAsync(dedupedBuffer, stoppingToken);
                            }
                            catch (Exception e)
                            {
                                _logger.LogError(e, "Failed to write changes to source.");
                            }
                            finally
                            {
                                lock (lockObj)
                                {
                                    hasPendingWrite = false;
                                }
                            }
                        }
                    }
                }
                finally
                {
                    // Wait for flush task to complete
                    await flushTask;
                }
            }
            catch (Exception ex)
            {
                if (ex is TaskCanceledException) return;
                
                _logger.LogError(ex, "Failed to listen for changes in source.");
                await Task.Delay(_retryTime, stoppingToken);
            }
        }
    }
}