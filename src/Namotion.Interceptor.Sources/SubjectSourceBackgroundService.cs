using System.Reactive.Linq;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Sources.Extensions;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Sources;

public class SubjectSourceBackgroundService : BackgroundService
{
    private readonly ISubjectSource _source;
    private readonly ILogger _logger;
    private readonly TimeSpan _bufferTime;
    private readonly TimeSpan _retryTime;

    private List<SubjectUpdate>? _beforeInitializationUpdates = [];

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

                using var disposable = await _source.InitializeAsync(UpdatePropertyValueFromSource, stoppingToken);
                var initialData = await _source.ReadFromSourceAsync(stoppingToken);
               
                lock (this)
                {
                    UpdatePropertyValueFromSource(initialData);

                    var beforeInitializationUpdates = _beforeInitializationUpdates;
                    _beforeInitializationUpdates = null;
                    
                    foreach (var data in beforeInitializationUpdates!)
                    {
                        UpdatePropertyValueFromSource(data);
                    }
                }
                
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
                    var update = SubjectUpdate.CreatePartialUpdateFromChanges(_source.Subject, changes);
                    await _source.WriteToSourceAsync(update, stoppingToken);
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
    
    private void UpdatePropertyValueFromSource(SubjectUpdate update)
    {
        lock (this)
        {
            if (_beforeInitializationUpdates is not null)
            {
                _beforeInitializationUpdates.Add(update);
            }
            else
            {
                _source.Subject.ApplySubjectSourceUpdate(update, _source);
            }
        }
    }
}
