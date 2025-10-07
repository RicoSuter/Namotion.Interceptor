using System.Reactive.Linq;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Sources.Paths;
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
                
                // listen for changes by ignoring changes from the source and buffering them into a single update
                await foreach (var changes in _context 
                    .GetPropertyChangedObservable()
                    .Where(change => change.Source != _source 
                        && _source.IsPropertyIncluded(change.Property.GetRegisteredProperty()))
                    .BufferChanges(_bufferTime)
                    .ToAsyncEnumerable()
                    .WithCancellation(stoppingToken))
                {
                    try
                    {
                        await _source.WriteToSourceAsync(changes, stoppingToken);
                    }
                    catch (Exception e)
                    {
                        // TODO: What do to here?
                        _logger.LogError(e, "Failed to write changes to source.");
                    }
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