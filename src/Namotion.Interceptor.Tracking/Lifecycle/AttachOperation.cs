using System.Runtime.ExceptionServices;

namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>
/// Tracks one attach operation of a <see cref="LifecycleInterceptor"/>, including the attaches nested in it, and runs
/// the callbacks registered during it once it is done. Accessed only under that interceptor's lock, except for the
/// action <see cref="End"/> returns.
/// </summary>
internal sealed class AttachOperation
{
    private readonly List<(Func<PropertyReference, Action<PropertyReference>?> Callback, PropertyReference Property, int AttachedSubjectCount)> _callbacks = [];
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
    /// Calls <paramref name="callback"/> for <paramref name="property"/> once the outermost operation is done, still
    /// under the lock, if a subject is attached after this call in the same operation. The action the callback returns
    /// is called for the property after the lock is released. The callback must not throw. Does nothing outside an
    /// operation.
    /// </summary>
    public void RunAtEndIfSubjectsAttachedLater(Func<PropertyReference, Action<PropertyReference>?> callback, in PropertyReference property)
    {
        if (_depth > 0)
        {
            _callbacks.Add((callback, property, _attachedSubjectCount));
        }
    }

    /// <summary>
    /// Ends an operation. Ending the outermost one runs the callbacks, also when the operation failed, since the subjects
    /// it attached before failing stay attached.
    /// </summary>
    /// <returns>What the callbacks left to run after the lock is released, or null.</returns>
    public Action? End()
    {
        try
        {
            return _depth == 1 ? RunCallbacks() : null;
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

    private Action? RunCallbacks()
    {
        List<(Action<PropertyReference> Action, PropertyReference Property)>? afterLock = null;

        // Indexed, because a callback may attach further subjects, which appends callbacks.
        for (var i = 0; i < _callbacks.Count; i++)
        {
            var (callback, property, attachedSubjectCount) = _callbacks[i];
            if (attachedSubjectCount != _attachedSubjectCount && callback(property) is { } action)
            {
                afterLock ??= [];
                afterLock.Add((action, property));
            }
        }

        return afterLock is null ? null : CreateAfterLockAction(afterLock);
    }

    // In its own method, so that the closure is only allocated when there is something to run: a lambda in
    // RunCallbacks would allocate it on every call.
    private static Action CreateAfterLockAction(List<(Action<PropertyReference> Action, PropertyReference Property)> actions)
    {
        return () => RunAfterLock(actions);
    }

    private static void RunAfterLock(List<(Action<PropertyReference> Action, PropertyReference Property)> actions)
    {
        ExceptionDispatchInfo? firstException = null;
        foreach (var (action, property) in actions)
        {
            try
            {
                action(property);
            }
            catch (Exception exception)
            {
                firstException ??= ExceptionDispatchInfo.Capture(exception);
            }
        }

        firstException?.Throw();
    }
}
