using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Tracking.Parent;

public class ParentTrackingHandler : ISubjectLifecycleHandler
{
    public void AttachSubject(SubjectLifecycleChange change)
    {
        if (change.Property.HasValue)
        {
            change.Subject.AddParent(change.Property.Value, change.Index);
        }
    }

    public void DetachSubject(SubjectLifecycleChange change)
    {
        if (change.Property.HasValue)
        {
            change.Subject.RemoveParent(change.Property.Value, change.Index);
        }
    }
}
