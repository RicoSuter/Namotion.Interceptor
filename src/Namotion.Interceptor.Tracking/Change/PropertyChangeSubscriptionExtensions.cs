namespace Namotion.Interceptor.Tracking.Change;

public static class PropertyChangeSubscriptionExtensions
{
    /// <summary>
    /// Subscribes an observer to changes of a single property (subject instance + name). Delivery is
    /// synchronous, on the writing thread, and dormant while the subject is not attached to a context
    /// with a <see cref="PropertyChangeInterceptor"/>. See <see cref="IPropertyChangeObserver"/> for the contract.
    /// </summary>
    public static IDisposable Subscribe(this PropertyReference property, IPropertyChangeObserver observer)
    {
        return PropertyChangeSubscription.Install(property, observer);
    }

    /// <summary>Delegate overload of <see cref="Subscribe(PropertyReference, IPropertyChangeObserver)"/>.</summary>
    public static IDisposable Subscribe(this PropertyReference property, PropertyChangeCallback callback)
    {
        return PropertyChangeSubscription.Install(property, new DelegateObserver(callback));
    }

    private sealed class DelegateObserver(PropertyChangeCallback callback) : IPropertyChangeObserver
    {
        public void OnChange(in SubjectPropertyChange change) => callback(in change);
    }
}
