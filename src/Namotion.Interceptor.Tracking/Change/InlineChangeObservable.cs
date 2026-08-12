namespace Namotion.Interceptor.Tracking.Change;

/// <summary>
/// Exposes a single property's changes as an <see cref="IObservable{T}"/> so Rx operators compose over a
/// per-property subscription. Subscribing installs one underlying per-property subscription per observer.
/// </summary>
/// <remarks>
/// Implements <see cref="IObservable{T}"/> directly rather than deriving from <c>ObservableBase&lt;T&gt;</c>
/// or being produced by an Rx operator. Both of those wrap observers in a decorator that disposes the
/// subscription when the handler throws, which would diverge from the contract of
/// <see cref="PropertyChangeSubscriptionExtensions.SubscribeInline(PropertyReference, IPropertyChangeObserver)"/>
/// that this type deliberately mirrors.
/// <para>
/// Deliberately not Rx-grammar-conformant: OnNext is forwarded straight from the writing thread, so
/// concurrent writers to one property call one observer's OnNext concurrently. It is not wrapped in
/// <c>Subject.Synchronize</c> the way <see cref="PropertyChangeInterceptor"/> wraps the context-level
/// observable, for the same reason it does not soften the throw contract: this is the inline channel wearing
/// an <see cref="IObservable{T}"/>. Callers that need the grammar apply <c>.Synchronize()</c> themselves; see
/// <see cref="PropertyChangeSubscriptionExtensions.GetInlineChangeObservable"/>.
/// </para>
/// </remarks>
internal sealed class InlineChangeObservable(PropertyReference property) : IObservable<SubjectPropertyChange>
{
    public IDisposable Subscribe(IObserver<SubjectPropertyChange> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);

        // OnError is never raised: the per-property channel has no error signal, and a throwing observer
        // is the observer's own problem, exactly as for an inline subscription.
        return property.SubscribeInline(new ObserverAdapter(observer));
    }

    // Adapting instead of passing a lambda drops the closure, the PropertyChangeCallback, and the internal
    // DelegateObserver per subscribe, plus one delegate hop per delivery on the write path.
    private sealed class ObserverAdapter(IObserver<SubjectPropertyChange> observer) : IPropertyChangeObserver
    {
        public void OnChange(in SubjectPropertyChange change) => observer.OnNext(change);
    }
}
