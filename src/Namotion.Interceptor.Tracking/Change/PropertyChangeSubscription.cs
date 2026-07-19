namespace Namotion.Interceptor.Tracking.Change;

/// <summary>
/// A subject-stored per-property subscription, held in Subject.Data[(propertyName, ListenersKey)]
/// as an element of an immutable copy-on-write array. Disposal is mandatory: the subject holds a
/// strong reference, and only Dispose decrements the process-wide count gating the idle write fast path.
/// </summary>
internal sealed class PropertyChangeSubscription : IDisposable
{
    // Short key to reduce dictionary hash cost on the hot lookup path (matches the ni.dpd convention).
    internal const string ListenersKey = "ni.pcl";

    private readonly string _propertyName;
    private IInterceptorSubject? _subject;                 // cleared on dispose
    internal IPropertyChangeObserver? Observer;            // read via Volatile.Read on dispatch; cleared on dispose
    private int _disposed;                                 // one-shot flag

    private PropertyChangeSubscription(IInterceptorSubject subject, string propertyName, IPropertyChangeObserver observer)
    {
        _subject = subject;
        _propertyName = propertyName;
        Observer = observer;
    }

    public static IDisposable Create(PropertyReference property, IPropertyChangeObserver observer)
    {
        var subject = property.Subject;
        var propertyName = property.Name;
        var subscription = new PropertyChangeSubscription(subject, propertyName, observer);
        var key = (propertyName, ListenersKey);

        // Increment before publishing so a concurrent write cannot read zero while this is installed.
        PropertyChangeSubscriptions.IncrementSubscriptionCount();

        var data = subject.Data;
        while (true)
        {
            if (data.TryGetValue(key, out var existing))
            {
                if (existing is not PropertyChangeSubscription[] current)
                {
                    // Foreign value under the reserved key: fail loud instead of livelocking,
                    // and roll back the count increment.
                    PropertyChangeSubscriptions.DecrementSubscriptionCount();
                    throw new InvalidOperationException(
                        $"The property data key '{ListenersKey}' on property '{propertyName}' of {subject.GetType().Name} " +
                        $"is reserved for property change subscriptions but holds a value of type " +
                        $"{existing?.GetType().ToString() ?? "null"}.");
                }

                var updated = CopyOnWriteArray.Add(current, subscription);

                // TryUpdate is the compare-and-swap: it replaces only if the stored value is still
                // the same array instance (EqualityComparer<object?>.Default = reference equality for arrays).
                if (data.TryUpdate(key, updated, current))
                {
                    return subscription;
                }
                // lost the race; retry
            }
            else if (data.TryAdd(key, new PropertyChangeSubscription[] { subscription }))
            {
                return subscription;
            }
            // else another thread added first; loop and TryUpdate
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return; // one-shot
        }

        var subject = _subject;
        if (subject is not null)
        {
            RemoveFromData(subject, _propertyName, this);
        }

        // Clear references so a retained handle pins neither subject nor observer; pair with dispatch Volatile.Read.
        Volatile.Write(ref _subject, null);
        Volatile.Write(ref Observer, null);

        // Decrement after removal: defense in depth, not required for correctness (no delivery
        // is owed to a subscription whose Dispose has begun).
        PropertyChangeSubscriptions.DecrementSubscriptionCount();
    }

    private static void RemoveFromData(IInterceptorSubject subject, string propertyName, PropertyChangeSubscription subscription)
    {
        var key = (propertyName, ListenersKey);
        var data = subject.Data;
        while (true)
        {
            if (!data.TryGetValue(key, out var existing) || existing is not PropertyChangeSubscription[] current)
            {
                return;
            }

            var index = Array.IndexOf(current, subscription);
            if (index < 0)
            {
                return;
            }

            if (current.Length == 1)
            {
                // Remove the whole entry with a compare-and-remove (reference equality on the array value).
                // TryRemovePropertyData removes (Name, key) only if the stored value reference-equals current.
                if (new PropertyReference(subject, propertyName).TryRemovePropertyData(ListenersKey, current))
                {
                    return;
                }
            }
            else
            {
                if (data.TryUpdate(key, CopyOnWriteArray.RemoveAt(current, index), current))
                {
                    return;
                }
            }
            // lost the race; retry
        }
    }

    // Dispatch reads the array snapshot and invokes each still-live observer by ref.
    public static void Dispatch(PropertyChangeSubscription[] subscriptions, in SubjectPropertyChange change)
    {
        for (var i = 0; i < subscriptions.Length; i++)
        {
            var observer = Volatile.Read(ref subscriptions[i].Observer);
            if (observer is not null)
            {
                observer.OnChange(in change);
            }
        }
    }
}
