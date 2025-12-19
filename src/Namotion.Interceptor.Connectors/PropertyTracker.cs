using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Connectors;

/// <summary>
/// Base class for tracking properties and handling lifecycle events.
/// Manages a set of tracked properties and automatically cleans up
/// when subjects are detached from the object graph.
/// </summary>
public class PropertyTracker : IDisposable
{
    private readonly HashSet<PropertyReference> _trackedProperties = [];
    private readonly ILogger? _logger;
    private readonly Lock _lock = new();

    private LifecycleInterceptor? _lifecycleInterceptor;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="PropertyTracker"/> class.
    /// </summary>
    /// <param name="logger">Optional logger for diagnostics.</param>
    public PropertyTracker(ILogger? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Gets the currently tracked properties.
    /// </summary>
    public IReadOnlyCollection<PropertyReference> TrackedProperties
    {
        get
        {
            lock (_lock)
            {
                return _trackedProperties.ToList();
            }
        }
    }

    /// <summary>
    /// Gets the logger instance.
    /// </summary>
    protected ILogger? Logger => _logger;

    /// <summary>
    /// Subscribes to lifecycle events from the subject's context.
    /// Call this during source initialization to receive detach notifications.
    /// </summary>
    /// <param name="root">The root subject to subscribe to.</param>
    public void SubscribeToLifecycle(IInterceptorSubject root)
    {
        ArgumentNullException.ThrowIfNull(root);
        SubscribeToLifecycle(root.Context);
    }

    /// <summary>
    /// Subscribes to lifecycle events from the context.
    /// Call this during source initialization to receive detach notifications.
    /// </summary>
    /// <param name="context">The context to subscribe to.</param>
    public void SubscribeToLifecycle(IInterceptorSubjectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        _lifecycleInterceptor = context.TryGetLifecycleInterceptor();
        if (_lifecycleInterceptor is not null)
        {
            _lifecycleInterceptor.SubjectDetached += OnSubjectDetached;
        }
    }

    /// <summary>
    /// Starts tracking a property.
    /// </summary>
    /// <param name="property">The property to track.</param>
    public void Track(PropertyReference property)
    {
        lock (_lock)
        {
            if (_trackedProperties.Add(property))
            {
                OnPropertyTracked(property);
            }
        }
    }

    /// <summary>
    /// Stops tracking a property.
    /// </summary>
    /// <param name="property">The property to untrack.</param>
    public void Untrack(PropertyReference property)
    {
        lock (_lock)
        {
            if (_trackedProperties.Remove(property))
            {
                OnPropertyUntracked(property);
            }
        }
    }

    /// <summary>
    /// Called when a property is added to tracking.
    /// Override to add custom behavior (e.g., setting source ownership).
    /// </summary>
    /// <param name="property">The property being tracked.</param>
    protected virtual void OnPropertyTracked(PropertyReference property)
    {
    }

    /// <summary>
    /// Called when a property is removed from tracking.
    /// Override to add custom behavior (e.g., removing source ownership).
    /// </summary>
    /// <param name="property">The property being untracked.</param>
    protected virtual void OnPropertyUntracked(PropertyReference property)
    {
    }

    /// <summary>
    /// Called when a subject is detaching, before its properties are untracked.
    /// Override to add custom cleanup behavior (e.g., removing subscription items).
    /// </summary>
    /// <param name="subject">The subject being detached.</param>
    protected virtual void OnSubjectDetaching(IInterceptorSubject subject)
    {
    }

    private void OnSubjectDetached(SubjectLifecycleChange change)
    {
        OnSubjectDetaching(change.Subject);

        List<PropertyReference> toRemove;
        lock (_lock)
        {
            toRemove = _trackedProperties
                .Where(p => p.Subject == change.Subject)
                .ToList();

            foreach (var property in toRemove)
            {
                _trackedProperties.Remove(property);
                OnPropertyUntracked(property);
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_lifecycleInterceptor is not null)
        {
            _lifecycleInterceptor.SubjectDetached -= OnSubjectDetached;
            _lifecycleInterceptor = null;
        }

        List<PropertyReference> remaining;
        lock (_lock)
        {
            remaining = _trackedProperties.ToList();
            _trackedProperties.Clear();
        }

        foreach (var property in remaining)
        {
            OnPropertyUntracked(property);
        }
    }
}
