using System.Runtime.CompilerServices;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tracking.Change;

/// <summary>
/// High-performance property change queue that distributes property changes to multiple subscriptions.
/// Thread-safe for concurrent property writes from multiple threads.
/// </summary>
public sealed class PropertyChangeQueue : IWriteInterceptor, IDisposable
{
    private volatile PropertyChangeQueueSubscription[] _subscriptions = [];
    private readonly Lock _subscriptionsModificationLock = new();

    /// <summary>
    /// Creates a new subscription to receive property changes.
    /// Each subscription has its own isolated queue and should be consumed by a single dedicated thread.
    /// </summary>
    /// <returns>A new subscription that must be disposed when no longer needed.</returns>
    public PropertyChangeQueueSubscription Subscribe()
    {
        var subscription = new PropertyChangeQueueSubscription(this);
        lock (_subscriptionsModificationLock)
        {
            var subscriptions = _subscriptions; // volatile read
            var updatedSubscriptions = new PropertyChangeQueueSubscription[subscriptions.Length + 1];
            Array.Copy(subscriptions, updatedSubscriptions, subscriptions.Length);
            updatedSubscriptions[subscriptions.Length] = subscription;
            _subscriptions = updatedSubscriptions;
        }
        return subscription;
    }

    public void Unsubscribe(PropertyChangeQueueSubscription subscription)
    {
        lock (_subscriptionsModificationLock)
        {
            var subscriptions = _subscriptions; // volatile read
            var index = Array.IndexOf(subscriptions, subscription);
            if (index >= 0)
            {
                var updatedSubscriptions = new PropertyChangeQueueSubscription[subscriptions.Length - 1];
                Array.Copy(subscriptions, 0, updatedSubscriptions, 0, index);
                Array.Copy(subscriptions, index + 1, updatedSubscriptions, index, subscriptions.Length - index - 1);
                _subscriptions = updatedSubscriptions;
            }
        }
    }

    public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
    {
        // ReSharper disable once InconsistentlySynchronizedField
        var subscriptions = _subscriptions; // volatile read
        if (subscriptions.Length == 0)
        {
            next(ref context);
            return;
        }

        var oldValue = context.CurrentValue;
        next(ref context);
        var newValue = context.GetFinalValue();

        var changeContext = SubjectChangeContext.Current;
        var propertyChange = SubjectPropertyChange.Create(
            context.Property,
            changeContext.Source,
            changeContext.ChangedTimestamp,
            changeContext.ReceivedTimestamp,
            oldValue,
            newValue);

        Enqueue(propertyChange);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Enqueue(SubjectPropertyChange change)
    {
        // ReSharper disable once InconsistentlySynchronizedField
        var subscriptions = _subscriptions; // volatile read
        for (int i = 0; i < subscriptions.Length; i++)
        {
            subscriptions[i].Enqueue(change); // never blocks
        }
    }

    public void Dispose()
    {
        // ReSharper disable once InconsistentlySynchronizedField
        var subscriptions = _subscriptions; // volatile read
        for (int i = 0; i < subscriptions.Length; i++)
        {
            subscriptions[i].Dispose();
        }
    }
}