using System.Reactive.Linq;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Sources.Extensions;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Sources;

public class SubjectSourceBackgroundService : BackgroundService, ISubjectSourceManager
{
    private readonly ISubjectSource _source;
    private readonly ILogger _logger;
    private readonly TimeSpan _bufferTime;
    private readonly TimeSpan _retryTime;

    private List<Action>? _beforeInitializationUpdates = [];

    public SubjectSourceBackgroundService(
        ISubjectSource source,
        ILogger logger,
        TimeSpan? bufferTime = null,
        TimeSpan? retryTime = null)
    {
        _source = source;
        _logger = logger;
        _bufferTime = bufferTime ?? TimeSpan.FromMilliseconds(8);
        _retryTime = retryTime ?? TimeSpan.FromSeconds(10);
    }

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
                using var disposable = await _source.InitializeAsync(this, stoppingToken);
                
                // read complete data set from source
                var initialData = await _source.ReadFromSourceAsync(stoppingToken);
                lock (this)
                {
                    EnqueueSubjectUpdate(() => _source.Subject.ApplySubjectSourceUpdate(initialData, _source));

                    // replaying previously buffered updates
                    var beforeInitializationUpdates = _beforeInitializationUpdates;
                    _beforeInitializationUpdates = null;
                    
                    foreach (var data in beforeInitializationUpdates!)
                    {
                        EnqueueSubjectUpdate(data);
                    }
                }
                
                // listen for changes by ignoring changes from the source and buffering them into a single update
                await foreach (var changes in _source
                    .Subject
                    .Context
                    .GetPropertyChangedObservable()
                    .Where(change => !change.IsChangingFromSource(_source))
                    .BufferChanges(_bufferTime)
                    .Where(changes => changes.Any())
                    .ToAsyncEnumerable()
                    .WithCancellation(stoppingToken))
                {
                    await _source.WriteToSourceAsync(changes, stoppingToken);
                }
            }
            catch (Exception ex)
            {
                if (ex is TaskCanceledException) return;
                
                _logger.LogError(ex, "Failed to listen for changes.");
                await Task.Delay(_retryTime, stoppingToken);
            }
        }
    }
    
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
                update.Invoke();
            }
        }
    }
}