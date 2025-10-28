using System.Runtime.CompilerServices;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tracking.Change;

public sealed class PropertyChangedChannel : IWriteInterceptor, IDisposable
{
    private volatile PropertyChangedChannelSubscription[] _subscriptions = [];
    private readonly Lock _gate = new();

    public PropertyChangedChannelSubscription Subscribe()
    {
        var subscription = new PropertyChangedChannelSubscription(this);
        lock (_gate)
        {
            var newArray = new PropertyChangedChannelSubscription[_subscriptions.Length + 1];
            Array.Copy(_subscriptions, newArray, _subscriptions.Length);
            newArray[_subscriptions.Length] = subscription;
            _subscriptions = newArray;
        }
        return subscription;
    }

    public void Unsubscribe(PropertyChangedChannelSubscription subscription)
    {
        lock (_gate)
        {
            var index = Array.IndexOf(_subscriptions, subscription);
            if (index >= 0)
            {
                var newArray = new PropertyChangedChannelSubscription[_subscriptions.Length - 1];
                Array.Copy(_subscriptions, 0, newArray, 0, index);
                Array.Copy(_subscriptions, index + 1, newArray, index, _subscriptions.Length - index - 1);
                _subscriptions = newArray;
            }
        }
    }

    public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
    {
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
        var subscriptions = _subscriptions; // volatile read
        for (int i = 0; i < subscriptions.Length; i++)
        {
            subscriptions[i].Enqueue(change); // never blocks
        }
    }

    public void Dispose()
    {
        var subscriptions = _subscriptions; // volatile read
        for (int i = 0; i < subscriptions.Length; i++)
        {
            subscriptions[i].Dispose();
        }
    }
}