using System.Collections.Concurrent;
using HomeBlaze.Abstractions.Storage;
using HomeBlaze.Core.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
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

    private readonly IInterceptorSubjectContext _context;
    private readonly ILogger<StorageService>? _logger;

    // Retry configuration (aligned with Sources library patterns from MqttConnectionMonitor)
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);
    private const int MaxRetryAttempts = 5;

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
                // Create a linked token that cancels after 100ms for periodic processing
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, timeoutCts.Token);

                // Process property changes (this blocks until an item is available or timeout)
                if (subscription.TryDequeue(out var change, linkedCts.Token))
                {
                    await ProcessChangeAsync(change, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                // Timeout from TryDequeue - continue
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
        // Check if this is a [Configuration] property using registry
        if (!IsConfigurationProperty(change.Property))
            return;

        var subject = change.Property.Subject;
        if (subject == null)
            return;

        _logger?.LogDebug("Configuration property changed: {Type}.{Property}",
            subject.GetType().Name, change.Property.Name);

        await WriteWithRetryAsync(subject, ct);
    }

    /// <summary>
    /// Writes subject with exponential backoff retry (pattern from Sources library).
    /// </summary>
    private async Task WriteWithRetryAsync(IInterceptorSubject subject, CancellationToken ct)
    {
        // O(1) lookup for the handler
        if (!_subjectHandlers.TryGetValue(subject, out var handler))
        {
            // Subject not registered - this is expected for subjects not tracked by any storage
            return;
        }

        var delay = InitialRetryDelay;

        for (int attempt = 0; attempt <= MaxRetryAttempts; attempt++)
        {
            try
            {
                if (await handler.WriteAsync(subject, ct))
                {
                    _logger?.LogDebug("Saved subject via {Handler}: {Type}",
                        handler.GetType().Name, subject.GetType().Name);
                }
                return; // Success
            }
            catch (IOException ex) when (attempt < MaxRetryAttempts)
            {
                // Exponential backoff with jitter (pattern from MqttConnectionMonitor)
                var jitter = Random.Shared.NextDouble() * 0.1 + 0.95; // 0.95 to 1.05
                var actualDelay = TimeSpan.FromMilliseconds(delay.TotalMilliseconds * jitter);

                _logger?.LogWarning(
                    "I/O error saving {Type} (attempt {Attempt}/{Max}), retrying in {Delay}ms: {Message}",
                    subject.GetType().Name, attempt + 1, MaxRetryAttempts, (int)actualDelay.TotalMilliseconds, ex.Message);

                await Task.Delay(actualDelay, ct);

                // Double delay for next attempt, capped at max
                delay = TimeSpan.FromMilliseconds(
                    Math.Min(delay.TotalMilliseconds * 2, MaxRetryDelay.TotalMilliseconds));
            }
            catch (IOException ex)
            {
                _logger?.LogError(ex, "Failed to save {Type} after {Max} retries",
                    subject.GetType().Name, MaxRetryAttempts);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error saving subject {Type}", subject.GetType().Name);
                return; // Don't retry non-IO errors
            }
        }
    }

    private static bool IsConfigurationProperty(PropertyReference property)
    {
        // Get the registered property directly from the reference using registry
        var regProperty = property.TryGetRegisteredProperty();
        return regProperty?.IsConfigurationProperty() ?? false;
    }
}
