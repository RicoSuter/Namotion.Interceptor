using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Tracking.Parent;

/// <summary>
/// Retained only as an ordering landmark while the handler merge is outstanding: several first-party
/// handlers position themselves relative to it. Parent state itself is owned and published by
/// <see cref="LifecycleInterceptor"/>, which is why this handler no longer records anything.
/// </summary>
[RunsBefore(typeof(ContextInheritanceHandler))]
public class ParentTrackingHandler : ILifecycleHandler
{
    public void HandleLifecycleChange(SubjectLifecycleChange change)
    {
    }
}
