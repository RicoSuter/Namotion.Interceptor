using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Tracking.Parent;

public class ParentTrackingHandler : ILifecycleHandler
{
    public void Attach(SubjectLifecycleChange change)
    {
        if (change.Property.HasValue)
        {
            change.Subject.AddParent(change.Property.Value, change.Index);
        }
    }

    public void Detach(SubjectLifecycleChange change)
    {
        if (change.Property.HasValue)
        {
            change.Subject.RemoveParent(change.Property.Value, change.Index);
        }
    }
}
