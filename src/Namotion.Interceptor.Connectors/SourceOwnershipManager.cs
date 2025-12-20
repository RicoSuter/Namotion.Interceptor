using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Connectors;

/// <summary>
/// Manages source ownership for properties with automatic lifecycle cleanup.
/// </summary>
/// <remarks>
/// This class maintains a set of properties owned by a source and handles:
/// <list type="bullet">
/// <item>Setting/removing source ownership when properties are claimed/released</item>
/// <item>Automatic cleanup when subjects are detached from the object graph</item>
/// <item>Cleanup of all owned properties when disposed</item>
/// </list>
/// </remarks>
public class SourceOwnershipManager : IDisposable
{
    private readonly ISubjectSource _source;
    private readonly Action<PropertyReference>? _onReleasing;
    private readonly Action<IInterceptorSubject>? _onSubjectDetaching;
    private readonly HashSet<PropertyReference> _properties = [];
    private readonly object _lock = new();
    private readonly ILogger? _logger;

    private LifecycleInterceptor? _lifecycle;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="SourceOwnershipManager"/> class.
    /// </summary>
    /// <param name="source">The source that owns the properties.</param>
    /// <param name="onReleasing">Optional callback invoked before a property is released.</param>
    /// <param name="onSubjectDetaching">Optional callback invoked before a subject's properties are released due to detachment.</param>
    /// <param name="logger">Optional logger for diagnostics.</param>
    public SourceOwnershipManager(
        ISubjectSource source,
        Action<PropertyReference>? onReleasing = null,
        Action<IInterceptorSubject>? onSubjectDetaching = null,
        ILogger? logger = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _onReleasing = onReleasing;
        _onSubjectDetaching = onSubjectDetaching;
        _logger = logger;
    }

    /// <summary>
    /// Gets the properties currently owned by this source.
    /// </summary>
    public IReadOnlyCollection<PropertyReference> Properties
    {
        get
        {
            lock (_lock)
            {
                return _properties.ToList();
            }
        }
    }

    /// <summary>
    /// Subscribes to lifecycle events for automatic cleanup on subject detach.
    /// </summary>
    /// <param name="context">The context to subscribe to.</param>
    /// <remarks>
    /// If the context does not have a <see cref="LifecycleInterceptor"/> configured,
    /// a warning is logged and properties will not be automatically cleaned up when
    /// subjects are detached. Call <c>WithLifecycle()</c> on the context to enable this.
    /// </remarks>
    public void SubscribeToLifecycle(IInterceptorSubjectContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        _lifecycle = context.TryGetLifecycleInterceptor();
        if (_lifecycle is null)
        {
            _logger?.LogWarning(
                "LifecycleInterceptor not configured for {SourceType}. Properties will not be " +
                "automatically cleaned up when subjects are detached. Call WithLifecycle() on the context.",
                _source.GetType().Name);
            return;
        }

        _lifecycle.SubjectDetached += OnSubjectDetached;
    }

    /// <summary>
    /// Claims source ownership for a property.
    /// </summary>
    /// <param name="property">The property to claim.</param>
    /// <returns>
    /// <c>true</c> if the property was successfully claimed or already owned by this source;
    /// <c>false</c> if the property is already owned by a different source.
    /// </returns>
    public bool ClaimSource(PropertyReference property)
    {
        lock (_lock)
        {
            if (!property.SetSource(_source))
            {
                return false;
            }

            _properties.Add(property);
            return true;
        }
    }

    /// <summary>
    /// Releases source ownership from a property.
    /// </summary>
    /// <param name="property">The property to release.</param>
    public void ReleaseSource(PropertyReference property)
    {
        lock (_lock)
        {
            if (_properties.Remove(property))
            {
                _onReleasing?.Invoke(property);
                property.RemoveSource(_source);
            }
        }
    }

    private void OnSubjectDetached(SubjectLifecycleChange change)
    {
        _onSubjectDetaching?.Invoke(change.Subject);

        lock (_lock)
        {
            var toRelease = _properties
                .Where(p => p.Subject == change.Subject)
                .ToList();

            foreach (var property in toRelease)
            {
                _properties.Remove(property);
                _onReleasing?.Invoke(property);
                property.RemoveSource(_source);
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

        if (_lifecycle is not null)
        {
            _lifecycle.SubjectDetached -= OnSubjectDetached;
            _lifecycle = null;
        }

        lock (_lock)
        {
            foreach (var property in _properties)
            {
                _onReleasing?.Invoke(property);
                property.RemoveSource(_source);
            }
            _properties.Clear();
        }
    }
}
