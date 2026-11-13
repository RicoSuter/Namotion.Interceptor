using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace HomeBlaze.Virtual;

/// <summary>
/// Recalculates the virtual properties that looked for a property of a subject that the subject did not have yet, once
/// that property is added at run time. Entries of a subject go when it detaches.
/// </summary>
/// <remarks>
/// Lifecycle handlers run under the lifecycle lock, so the recalculation runs on the thread pool. The awaiter's own lock
/// is never held while the lifecycle lock or the registry lock is taken.
/// </remarks>
public sealed class VirtualPropertyAwaiter : ILifecycleHandler, IPropertyLifecycleHandler
{
    private readonly Lock _lock = new();
    private readonly Dictionary<IInterceptorSubject, Dictionary<string, HashSet<PropertyReference>>> _waiting = new(ReferenceEqualityComparer.Instance);

    /// <summary>Creates the awaiter and adds it to the handlers of the context.</summary>
    public VirtualPropertyAwaiter(IInterceptorSubjectContext context)
    {
        context.AddService(this);
    }

    /// <summary>Recalculates <paramref name="property"/> once <paramref name="subject"/> gets a property <paramref name="name"/>.</summary>
    internal void Await(IInterceptorSubject subject, string name, PropertyReference property)
    {
        lock (_lock)
        {
            if (!_waiting.TryGetValue(subject, out var names))
            {
                _waiting[subject] = names = new Dictionary<string, HashSet<PropertyReference>>(StringComparer.Ordinal);
            }

            if (!names.TryGetValue(name, out var properties))
            {
                names[name] = properties = [];
            }

            properties.Add(property);
        }

        // The miss was recorded before this call, so the property may have been added in between, and the registry adds
        // a property before it attaches it. Checked outside the lock, because the registry takes its own lock.
        var registered = subject.TryGetRegisteredSubject();
        if (registered is null)
        {
            // The subject left the graph, so its detach may have run before the registration.
            Remove(subject, name, property);
        }
        else if (registered.TryGetProperty(name) is not null && Remove(subject, name, property))
        {
            ThreadPool.UnsafeQueueUserWorkItem(static property => property.RecalculateDerivedProperty(), property, preferLocal: false);
        }
    }

    /// <summary>Stops waiting for anything on behalf of <paramref name="property"/>.</summary>
    internal void Forget(PropertyReference property)
    {
        lock (_lock)
        {
            // Removing while enumerating a dictionary is allowed.
            foreach (var (subject, names) in _waiting)
            {
                foreach (var (name, properties) in names)
                {
                    if (properties.Remove(property) && properties.Count == 0)
                    {
                        names.Remove(name);
                    }
                }

                if (names.Count == 0)
                {
                    _waiting.Remove(subject);
                }
            }
        }
    }

    /// <inheritdoc />
    public void HandleLifecycleChange(SubjectLifecycleChange change)
    {
        if (change.IsContextDetach)
        {
            lock (_lock)
            {
                _waiting.Remove(change.Subject);
            }
        }
    }

    /// <inheritdoc />
    public void AttachProperty(SubjectPropertyLifecycleChange change)
    {
        if (!change.Property.Metadata.IsDynamic)
        {
            return;
        }

        HashSet<PropertyReference>? due;
        lock (_lock)
        {
            if (!_waiting.TryGetValue(change.Subject, out var names) || !names.Remove(change.Property.Name, out due))
            {
                return;
            }

            if (names.Count == 0)
            {
                _waiting.Remove(change.Subject);
            }
        }

        // No longer in the dictionary, so nothing changes it any more.
        ThreadPool.UnsafeQueueUserWorkItem(static properties =>
        {
            foreach (var property in properties)
            {
                property.RecalculateDerivedProperty();
            }
        }, due, preferLocal: false);
    }

    /// <inheritdoc />
    public void DetachProperty(SubjectPropertyLifecycleChange change)
    {
    }

    // True when the registration was still there, so this caller and not AttachProperty owns the recalculation.
    private bool Remove(IInterceptorSubject subject, string name, PropertyReference property)
    {
        lock (_lock)
        {
            if (!_waiting.TryGetValue(subject, out var names) ||
                !names.TryGetValue(name, out var properties) ||
                !properties.Remove(property))
            {
                return false;
            }

            if (properties.Count == 0)
            {
                names.Remove(name);
                if (names.Count == 0)
                {
                    _waiting.Remove(subject);
                }
            }

            return true;
        }
    }
}
