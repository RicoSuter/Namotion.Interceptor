namespace Namotion.Interceptor.Tracking.Change;

/// <summary>
/// Per-property change observer. What it must guarantee depends on how it was subscribed.
/// <para>
/// Unscheduled, through
/// <see cref="PropertyChangeSubscriptionExtensions.Subscribe(PropertyReference, IPropertyChangeObserver)"/>:
/// OnChange runs on the writing thread, inside the write, outside the subject lock. Implementations MUST be
/// thread-safe (they may be invoked concurrently), fast, non-blocking, and MUST NOT throw, because a throw
/// propagates out of the setter and suppresses later deliveries for that write.
/// </para>
/// <para>
/// Scheduled, through
/// <see cref="PropertyChangeSubscriptionExtensions.Subscribe(PropertyReference, IPropertyChangeObserver, System.Reactive.Concurrency.IScheduler, Action{Exception})"/>:
/// OnChange runs on the scheduler and MAY throw, which is reported to the subscription's <c>onError</c> and
/// leaves the subscription live. It is never re-entered within one subscription, so it needs no
/// synchronization of its own, but one instance shared across several subscriptions is still invoked
/// concurrently and must synchronize.
/// </para>
/// Deliveries may arrive out of commit order under concurrent writes to the same property in both cases;
/// re-read the property if you need the current value.
/// </summary>
public interface IPropertyChangeObserver
{
    /// <summary>
    /// Invoked when a subscribed property changes, on the writing thread or on the subscription's scheduler.
    /// </summary>
    /// <param name="change">The property change that occurred.</param>
    void OnChange(in SubjectPropertyChange change);
}
