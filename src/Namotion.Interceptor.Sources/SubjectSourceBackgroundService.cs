using System.Reactive.Linq;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Sources;

public class SubjectSourceBackgroundService<TSubject> : BackgroundService
    where TSubject : IInterceptorSubject
{
    private readonly TSubject _subject;
    private readonly ISubjectSource _source;
    private readonly ILogger _logger;
    private readonly TimeSpan _bufferTime;
    private readonly TimeSpan _retryTime;

    private HashSet<string>? _initializedProperties;

    public SubjectSourceBackgroundService(
        TSubject subject,
        ISubjectSource source,
        ILogger logger,
        TimeSpan? bufferTime = null,
        TimeSpan? retryTime = null)
    {
        _subject = subject;
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
                // TODO: Currently newly added properties/trackable are not automatically tracked/subscribed to

                var propertiesWithSetter = _subject
                    .GetSubjectAndChildProperties()
                    .Where(p => p.HasSetter)
                    .Select(p => new PropertyPathReference(p.Property,
                        _source.TryGetSourcePropertyPath(p.Property) ?? string.Empty,
                        p.HasGetter ? p.GetValue() : null))
                    .Where(p => p.Path != string.Empty)
                    .ToList();

                lock (this)
                {
                    _initializedProperties = [];
                }

                // subscribe first and mark all properties as initialized which are updated before the read has completed 
                using var disposable = await _source.InitializeAsync(propertiesWithSetter, UpdatePropertyValueFromSource, stoppingToken);

                // read all properties (subscription during read will later be ignored)
                var initialValues = await _source.ReadAsync(propertiesWithSetter, stoppingToken);
                lock (this)
                {
                    // ignore properties which have been updated via subscription
                    foreach (var value in initialValues
                        .Where(v => !_initializedProperties.Contains(v.Path)))
                    {
                        UpdatePropertyValueFromSource(value);
                    }

                    _initializedProperties = null;
                }
                
                await foreach (var changes in _subject
                    .Context
                    .GetPropertyChangedObservable()
                    .Where(change => 
                        !change.IsChangingFromSource(_source) && 
                       _source.TryGetSourcePropertyPath(change.Property) != null)
                    .BufferChanges(_bufferTime)
                    .Where(changes => changes.Any())
                    .ToAsyncEnumerable()
                    .WithCancellation(stoppingToken))
                {
                    var values = changes
                        .Select(c => new PropertyPathReference(
                            c.Property, _source.TryGetSourcePropertyPath(c.Property)!, c.NewValue));

                    await _source.WriteAsync(values, stoppingToken);
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

    private void UpdatePropertyValueFromSource(PropertyPathReference property)
    {
        try
        {
            MarkPropertyAsInitialized(property.Path);
            property.Property.SetValueFromSource(_source, property.Value);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set value of property {PropertyName} of type {Type}.",
                property.Property.Subject.GetType().FullName, property.Property.Name);
        }
    }

    private void MarkPropertyAsInitialized(string sourcePath)
    {
        if (_initializedProperties is not null)
        {
            lock (this)
            {
                _initializedProperties?.Add(sourcePath);
            }
        }
    }
}
