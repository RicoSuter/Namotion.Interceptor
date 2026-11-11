namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>
/// Tracks one attach operation of a <see cref="LifecycleInterceptor"/>, including the attaches nested in it, and runs
/// the callbacks registered during it once it is done. Accessed only under that interceptor's lock.
/// </summary>
internal sealed class AttachOperation
{
    private readonly List<(Action<PropertyReference> Callback, PropertyReference Property, int AttachedSubjectCount)> _callbacks = [];
    private int _depth;
    private int _attachedSubjectCount;

    public void Begin()
    {
        _depth++;
    }

    public void OnSubjectAttached()
    {
        _attachedSubjectCount++;
    }

    /// <summary>
    /// Calls <paramref name="callback"/> for <paramref name="property"/> once the outermost operation is done, if a
    /// subject is attached after this call in the same operation. Returns false outside an operation.
    /// </summary>
    public bool TryRunWhenLaterSubjectsAttached(Action<PropertyReference> callback, in PropertyReference property)
    {
        if (_depth == 0)
        {
            return false;
        }

        _callbacks.Add((callback, property, _attachedSubjectCount));
        return true;
    }

    /// <summary>
    /// Ends an operation. Ending the outermost one runs the callbacks, also when the operation failed, since the subjects
    /// it attached before failing stay attached.
    /// </summary>
    public void End()
    {
        try
        {
            if (_depth != 1)
            {
                return;
            }

            // Indexed, because a callback may attach further subjects, which appends callbacks.
            for (var i = 0; i < _callbacks.Count; i++)
            {
                var (callback, property, attachedSubjectCount) = _callbacks[i];
                if (attachedSubjectCount != _attachedSubjectCount)
                {
                    callback(property);
                }
            }
        }
        finally
        {
            if (--_depth == 0)
            {
                _callbacks.Clear();
                _attachedSubjectCount = 0;
            }
        }
    }
}
