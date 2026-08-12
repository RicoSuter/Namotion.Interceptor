namespace Namotion.Interceptor.Tracking.Change;

/// <summary>
/// Exposes a single property's changes as an <see cref="IObservable{T}"/> so Rx operators compose over a
/// per-property subscription. Subscribing installs one underlying per-property subscription per observer.
/// </summary>
/// <remarks>
/// Implements <see cref="IObservable{T}"/> directly rather than deriving from <c>ObservableBase&lt;T&gt;</c>
/// or being produced by an Rx operator. Both of those wrap observers in a decorator that disposes the
/// subscription when the handler throws, which would diverge from the contract of
/// <see cref="PropertyChangeSubscriptionExtensions.Subscribe(PropertyReference, IPropertyChangeObserver)"/>
/// that this type deliberately mirrors.
/// </remarks>
internal sealed class SynchronousChangeObservable(PropertyReference property) : IObservable<SubjectPropertyChange>
{
    public IDisposable Subscribe(IObserver<SubjectPropertyChange> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);

        // OnError is never raised: the per-property channel has no error signal, and a throwing observer
        // is the observer's own problem, exactly as for an unscheduled subscription.
        return property.Subscribe((in SubjectPropertyChange change) => observer.OnNext(change));
    }
}
