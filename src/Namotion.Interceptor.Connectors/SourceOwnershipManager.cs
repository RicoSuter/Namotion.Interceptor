using Namotion.Interceptor.Registry;
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
    private readonly Lock _lock = new();
    private readonly LifecycleInterceptor _lifecycle;

    private int _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="SourceOwnershipManager"/> class.
    /// </summary>
    /// <param name="source">The source that owns the properties.</param>
    /// <param name="onReleasing">Optional callback invoked before a property is released.</param>
    /// <param name="onSubjectDetaching">Optional callback invoked before a subject's properties are released due to detachment.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the source's context does not have a <see cref="LifecycleInterceptor"/> configured.
    /// Call <c>WithLifecycle()</c> on the context to enable lifecycle tracking.
    /// </exception>
    public SourceOwnershipManager(
        ISubjectSource source,
        Action<PropertyReference>? onReleasing = null,
        Action<IInterceptorSubject>? onSubjectDetaching = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _onReleasing = onReleasing;
        _onSubjectDetaching = onSubjectDetaching;

        var context = source.RootSubject.Context;
        _lifecycle = context.TryGetLifecycleInterceptor()
            ?? throw new InvalidOperationException(
                $"LifecycleInterceptor not configured for {source.GetType().Name}. " +
                "Call WithLifecycle() on the context to enable automatic cleanup when subjects are detached.");

        _lifecycle.SubjectDetaching += OnSubjectDetaching;
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
                return _properties.ToArray();
            }
        }
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

    /// <summary>
    /// Releases ownership of every owned property whose subject is no longer registered (detached
    /// from the object graph). This reconciles the owned set after an operation that can claim a
    /// property whose subject detaches concurrently: a bulk re-claim (see callers) snapshots the
    /// graph and then claims each property, so a subject detaching between snapshot and claim is
    /// re-added <em>after</em> its <see cref="LifecycleInterceptor.SubjectDetaching"/> event already
    /// fired. Without this reconcile that property would never be released, retaining the detached
    /// subject and its subtree.
    /// </summary>
    /// <returns>The number of detached properties released.</returns>
    public int ReleaseDetachedSubjects()
    {
        // Requires a registry to distinguish detached subjects; if the source root itself is not
        // registered there is no registry (or state is not loaded yet) and nothing to reconcile.
        if (_source.RootSubject.TryGetRegisteredSubject() is null)
        {
            return 0;
        }

        lock (_lock)
        {
            List<PropertyReference>? detached = null;
            foreach (var property in _properties)
            {
                if (property.Subject.TryGetRegisteredSubject() is null)
                {
                    (detached ??= []).Add(property);
                }
            }

            if (detached is null)
            {
                return 0;
            }

            foreach (var property in detached)
            {
                _properties.Remove(property);
                _onReleasing?.Invoke(property);
                property.RemoveSource(_source);
            }

            return detached.Count;
        }
    }

    private void OnSubjectDetaching(SubjectLifecycleChange change)
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
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _lifecycle.SubjectDetaching -= OnSubjectDetaching;

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
