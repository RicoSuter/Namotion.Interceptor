namespace Namotion.Interceptor.Tracking.Lifecycle;

/// <summary>
/// Tracks the running attach or detach operation of a <see cref="LifecycleInterceptor"/>, including the operations nested in
/// it, and runs the callbacks registered during it once it is done. <see cref="Enter"/> takes that interceptor's lock;
/// everything else is called under it.
/// </summary>
internal sealed class AttachOperation
{
    // Above this, the list of an unusually large operation is released rather than kept for the next one.
    private const int MaxRetainedCallbacks = 4096;

    // The lifecycle locks this thread holds through scopes, of every interceptor, and what the callbacks left to run
    // once it holds none. A subject in several graphs nests the operations of several interceptors.
    [ThreadStatic]
    private static int _heldLocks;

    [ThreadStatic]
    private static List<(Action<PropertyReference> Action, PropertyReference Property)>? _afterLocks;

    [ThreadStatic]
    private static AttachOperation? _current;

    private readonly List<(Func<PropertyReference, Action<PropertyReference>?> Callback, PropertyReference Property, int AttachedSubjectCount)> _callbacks = [];
    private int _depth;
    private int _attachedSubjectCount;

    /// <summary>
    /// Takes <paramref name="lifecycleLock"/> and begins an operation. Disposing the scope ends the operation and
    /// releases the lock. Ending the outermost operation runs the callbacks, also when the operation failed, since the
    /// subjects it attached before failing stay attached. Once the thread holds no lifecycle lock anymore, it runs what
    /// the callbacks left to run.
    /// </summary>
    public Scope Enter(object lifecycleLock)
    {
        Monitor.Enter(lifecycleLock);
        _depth++;
        _heldLocks++;
        var outerOperation = _current;
        _current = this;
        return new Scope(this, lifecycleLock, outerOperation);
    }

    /// <summary>
    /// The innermost operation that runs on this thread, or null: the one of the interceptor that attaches subjects
    /// and their properties right now.
    /// </summary>
    public static AttachOperation? Current => _current;

    public readonly struct Scope(AttachOperation operation, object lifecycleLock, AttachOperation? outerOperation) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                if (operation._depth == 1)
                {
                    operation.RunCallbacks();
                }
            }
            finally
            {
                if (--operation._depth == 0)
                {
                    operation.Reset();
                }

                _current = outerOperation;
                Monitor.Exit(lifecycleLock);
                _heldLocks--;
            }

            if (_heldLocks == 0 && _afterLocks is { } actions)
            {
                // Taken over first, because an action may start an operation that leaves its own.
                _afterLocks = null;
                foreach (var (action, property) in actions)
                {
                    action(property);
                }
            }
        }
    }

    public void OnSubjectAttached()
    {
        _attachedSubjectCount++;
    }

    /// <summary>
    /// Calls <paramref name="callback"/> for <paramref name="property"/> once the outermost operation is done, still
    /// under the lock, if a subject is attached after this call in the same operation. The action the callback returns
    /// is called for the property once the thread holds no lifecycle lock. Neither of the two may throw.
    /// </summary>
    public void RunAtEndIfSubjectsAttachedLater(Func<PropertyReference, Action<PropertyReference>?> callback, in PropertyReference property)
    {
        _callbacks.Add((callback, property, _attachedSubjectCount));
    }

    private void Reset()
    {
        _callbacks.Clear();
        if (_callbacks.Capacity > MaxRetainedCallbacks)
        {
            _callbacks.Capacity = 0;
        }

        _attachedSubjectCount = 0;
    }

    private void RunCallbacks()
    {
        // Indexed, because a callback may attach further subjects, which appends callbacks.
        for (var i = 0; i < _callbacks.Count; i++)
        {
            var (callback, property, attachedSubjectCount) = _callbacks[i];
            if (attachedSubjectCount != _attachedSubjectCount && callback(property) is { } action)
            {
                (_afterLocks ??= []).Add((action, property));
            }
        }
    }
}
