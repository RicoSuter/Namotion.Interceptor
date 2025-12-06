using System.Collections.Concurrent;
using System.Reflection;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.Tracking.Change;

namespace HomeBlaze.Core.Services;

/// <summary>
/// Background service that listens to property changes and auto-saves configurations.
/// Uses PropertyChangeQueue for high-performance change detection.
/// </summary>
public class StorageService : BackgroundService
{
    // Direct subject-to-handler mapping for O(1) lookup
    private readonly ConcurrentDictionary<IInterceptorSubject, ISubjectStorageHandler> _subjectHandlers = new();

    private readonly ConcurrentQueue<(IInterceptorSubject Subject, int RetryCount)> _retryQueue = new();
    private readonly IInterceptorSubjectContext _context;
    private readonly ILogger<StorageService>? _logger;

    // Cache for [Configuration] property lookup per type
    private static readonly ConcurrentDictionary<Type, HashSet<string>> _configurationPropertiesCache = new();

    private const int MaxRetries = 5;
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30)
    ];

    public StorageService(
        IInterceptorSubjectContext context,
        ILogger<StorageService>? logger = null)
    {
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Registers a subject with its storage handler for auto-persistence.
    /// Thread-safe via ConcurrentDictionary.
    /// </summary>
    /// <param name="subject">The subject to track.</param>
    /// <param name="handler">The handler responsible for persisting this subject.</param>
    public void RegisterSubject(IInterceptorSubject subject, ISubjectStorageHandler handler)
    {
        _subjectHandlers[subject] = handler;
        _logger?.LogDebug("Registered subject {Type} with handler {Handler}",
            subject.GetType().Name, handler.GetType().Name);
    }

    /// <summary>
    /// Unregisters a subject from auto-persistence.
    /// Thread-safe via ConcurrentDictionary.
    /// </summary>
    /// <param name="subject">The subject to stop tracking.</param>
    public void UnregisterSubject(IInterceptorSubject subject)
    {
        if (_subjectHandlers.TryRemove(subject, out _))
        {
            _logger?.LogDebug("Unregistered subject {Type}", subject.GetType().Name);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Delay slightly to ensure host startup completes before we start our blocking loop.
        // This is critical because TryDequeue uses blocking Wait() calls.
        await Task.Delay(100, stoppingToken);

        _logger?.LogInformation("StorageService background loop starting");

        var queue = _context.TryGetService<PropertyChangeQueue>();
        if (queue == null)
        {
            _logger?.LogWarning("PropertyChangeQueue not found in context. StorageService will not auto-save.");
            return;
        }

        using var subscription = queue.Subscribe();
        _logger?.LogDebug("StorageService subscribed to PropertyChangeQueue");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Create a linked token that cancels after 100ms for periodic retry processing
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, timeoutCts.Token);

                // Process property changes (this blocks until an item is available or timeout)
                if (subscription.TryDequeue(out var change, linkedCts.Token))
                {
                    await ProcessChangeAsync(change, stoppingToken);
                }

                // Process retry queue periodically
                await ProcessRetryQueueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                // Timeout from TryDequeue - continue to process retry queue
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error in StorageService loop");
            }
        }

        _logger?.LogInformation("StorageService stopped");
    }

    private async Task ProcessChangeAsync(SubjectPropertyChange change, CancellationToken ct)
    {
        // Check if this is a [Configuration] property
        if (!IsConfigurationProperty(change.Property))
            return;

        var subject = change.Property.Subject;
        if (subject == null)
            return;

        _logger?.LogDebug("Configuration property changed: {Type}.{Property}",
            subject.GetType().Name, change.Property.Name);

        await WriteSubjectAsync(subject, retryCount: 0, ct);
    }

    private async Task WriteSubjectAsync(IInterceptorSubject subject, int retryCount, CancellationToken ct)
    {
        // O(1) lookup for the handler
        if (!_subjectHandlers.TryGetValue(subject, out var handler))
        {
            // Subject not registered - this is expected for subjects not tracked by any storage
            return;
        }

        try
        {
            if (await handler.WriteAsync(subject, ct))
            {
                _logger?.LogDebug("Saved subject via {Handler}: {Type}",
                    handler.GetType().Name, subject.GetType().Name);
            }
        }
        catch (IOException ex)
        {
            _logger?.LogWarning(ex, "Transient I/O error saving {Type}, will retry", subject.GetType().Name);

            if (retryCount < MaxRetries)
            {
                _retryQueue.Enqueue((subject, retryCount + 1));
            }
            else
            {
                _logger?.LogError("Max retries exceeded for {Type}", subject.GetType().Name);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error saving subject {Type}", subject.GetType().Name);
        }
    }

    private async Task ProcessRetryQueueAsync(CancellationToken ct)
    {
        if (_retryQueue.IsEmpty)
            return;

        // Process up to 10 retries per iteration
        for (int i = 0; i < 10 && _retryQueue.TryDequeue(out var item); i++)
        {
            var (subject, retryCount) = item;

            // Apply exponential backoff delay
            var delayIndex = Math.Min(retryCount - 1, RetryDelays.Length - 1);
            await Task.Delay(RetryDelays[delayIndex], ct);

            await WriteSubjectAsync(subject, retryCount, ct);
        }
    }

    private static bool IsConfigurationProperty(PropertyReference property)
    {
        var subjectType = property.Subject?.GetType();
        if (subjectType == null)
            return false;

        var configProperties = GetConfigurationProperties(subjectType);
        return configProperties.Contains(property.Name);
    }

    private static HashSet<string> GetConfigurationProperties(Type subjectType)
    {
        return _configurationPropertiesCache.GetOrAdd(subjectType, static type =>
        {
            return type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.GetCustomAttribute<ConfigurationAttribute>() != null)
                .Select(p => p.Name)
                .ToHashSet(StringComparer.Ordinal);
        });
    }
}
