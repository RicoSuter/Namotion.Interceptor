namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>
/// Collects property attaches that a handler postpones until every subject entering the graph in the current
/// operation of a <see cref="LifecycleInterceptor"/> is attached. Accessed only under that interceptor's lock.
/// </summary>
internal sealed class PropertyAttachDeferral
{
    private readonly List<(IPropertyLifecycleHandler Handler, PropertyReference Property)> _attaches = [];
    private int _depth;

    public void Begin()
    {
        _depth++;
    }

    /// <summary>
    /// Defers the rest of a property attach and reports whether it did. Once the outermost operation has attached
    /// every subject, the handler's <see cref="IPropertyLifecycleHandler.AttachProperty"/> is called again for the
    /// property, with a change that cannot be deferred.
    /// </summary>
    public bool TryDefer(IPropertyLifecycleHandler handler, in PropertyReference property)
    {
        if (_depth == 0)
        {
            return false;
        }

        _attaches.Add((handler, property));
        return true;
    }

    /// <summary>
    /// Runs the deferred attaches when called from the outermost operation.
    /// </summary>
    public void RunIfOutermost(Dictionary<IInterceptorSubject, PropertyReferenceSet> attachedSubjects)
    {
        if (_depth != 1)
        {
            return;
        }

        // Indexed, because a deferred attach may attach further subjects whose deferred attaches are appended.
        for (var i = 0; i < _attaches.Count; i++)
        {
            var (handler, property) = _attaches[i];

            // Skips a subject that left the graph again in the same operation.
            if (attachedSubjects.ContainsKey(property.Subject))
            {
                handler.AttachProperty(new SubjectPropertyLifecycleChange(property.Subject, property));
            }
        }
    }

    public void End()
    {
        if (--_depth == 0)
        {
            _attaches.Clear();
        }
    }
}
