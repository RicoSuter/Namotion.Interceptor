using System.Runtime.CompilerServices;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tracking.Change;

public sealed class PropertyChangedChannel : IWriteInterceptor, IDisposable
{
    private volatile PropertyChangedChannelSubscription[] _subscriptions = [];
    private readonly Lock _subscriptionsModificationLock = new();

    public PropertyChangedChannelSubscription Subscribe()
    {
        var subscription = new PropertyChangedChannelSubscription(this);
        lock (_subscriptionsModificationLock)
        {
            var subscriptions = _subscriptions; // volatile read
            var updatedSubscriptions = new PropertyChangedChannelSubscription[subscriptions.Length + 1];
            Array.Copy(subscriptions, updatedSubscriptions, subscriptions.Length);
            updatedSubscriptions[subscriptions.Length] = subscription;
            _subscriptions = updatedSubscriptions;
        }
        return subscription;
    }

    public void Unsubscribe(PropertyChangedChannelSubscription subscription)
    {
        lock (_subscriptionsModificationLock)
        {
            var subscriptions = _subscriptions; // volatile read
            var index = Array.IndexOf(subscriptions, subscription);
            if (index >= 0)
            {
                var updatedSubscriptions = new PropertyChangedChannelSubscription[subscriptions.Length - 1];
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