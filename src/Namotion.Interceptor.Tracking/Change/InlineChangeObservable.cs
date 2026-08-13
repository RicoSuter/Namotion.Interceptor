namespace Namotion.Interceptor.Tracking.Change;

// Implements IObservable<T> directly rather than deriving from ObservableBase<T> or being produced by an Rx
// operator: both wrap the observer in a decorator that disposes the subscription when the handler throws,
// which would soften the SubscribeInline contract this type deliberately mirrors, unserialized OnNext and
// setter-bound handler exceptions included.
internal sealed class InlineChangeObservable(PropertyReference property) : IObservable<SubjectPropertyChange>
{
    public IDisposable Subscribe(IObserver<SubjectPropertyChange> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);

        // OnError is never raised: the per-property channel has no error signal.
        return property.SubscribeInline(new ObserverAdapter(observer));
    }

    // Adapting instead of passing a lambda drops the closure, the PropertyChangeCallback and the
    // DelegateObserver per subscribe, plus one delegate hop per delivery on the write path.
    private sealed class ObserverAdapter(IObserver<SubjectPropertyChange> observer) : IPropertyChangeObserver
    {
        public void OnChange(in SubjectPropertyChange change) => observer.OnNext(change);
    }
}
