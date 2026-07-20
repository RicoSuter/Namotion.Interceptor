namespace Namotion.Interceptor.Tracking.Change;

/// <summary>
/// Synchronous per-property change observer. OnChange runs on the writing thread, inside the write,
/// outside the subject lock. Implementations MUST be thread-safe (they may be invoked concurrently),
/// fast, non-blocking, and MUST NOT throw. Deliveries may arrive out of commit order under concurrent
/// writes to the same property; re-read the property if you need the current value.
/// </summary>
public interface IPropertyChangeObserver
{
    /// <summary>
    /// Invoked synchronously when a subscribed property changes.
    /// </summary>
    /// <param name="change">The property change that occurred.</param>
    void OnChange(in SubjectPropertyChange change);
}
