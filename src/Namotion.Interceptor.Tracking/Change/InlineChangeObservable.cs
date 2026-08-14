namespace Namotion.Interceptor.Tracking.Change;

// Implements IObservable<T> directly rather than deriving from ObservableBase<T> or being produced by an Rx
// operator: both wrap the observer in a decorator that disposes the subscription when the handler throws,
// which would soften the SubscribeInline contract this type deliberately mirrors, setter-bound handler
// exceptions included.
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
        // IObservable<T> requires serialized notifications, and delivery here is on the writing thread, so
        // concurrent writers to one property would otherwise raise OnNext concurrently on one observer. The
        // gate is per subscriber because each subscriber is an independent sequence, and only this adapter,
        // which only GetInlineChangeObservable builds, pays for it. It is held across the handler, so a
        // handler that writes a property observed by another such observable can produce a lock-ordering
        // cycle; that hazard is accepted because the context-level observable has had the same exposure
        // since long before this, though at a lower degree: its Subject.Synchronize() gate is one per
        // context, so a cycle there needs two contexts, where two subscribers in one context suffice here.
        // The remarks on GetInlineChangeObservable say so, and send a writing handler elsewhere.
        private readonly Lock _notificationGate = new();

        public void OnChange(in SubjectPropertyChange change)
        {
            lock (_notificationGate)
            {
                observer.OnNext(change);
            }
        }
    }
}
