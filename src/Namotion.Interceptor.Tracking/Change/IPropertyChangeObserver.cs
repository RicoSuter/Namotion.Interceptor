namespace Namotion.Interceptor.Tracking.Change;

/// <summary>
/// Synchronous per-property change observer. OnChange runs on the writing thread, inside the write,
/// outside the subject lock. Implementations MUST be thread-safe (they may be invoked concurrently),
/// fast, non-blocking, and MUST NOT throw. Deliveries may arrive out of commit order under concurrent
/// writes to the same property; re-read the property if you need the current value.
/// </summary>
public interface IPropertyChangeObserver
{
    void OnChange(in SubjectPropertyChange change);
}
