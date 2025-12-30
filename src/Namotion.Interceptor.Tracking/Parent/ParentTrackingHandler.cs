using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Tracking.Parent;

[RunsBefore(typeof(ContextInheritanceHandler))]
public class ParentTrackingHandler : IReferenceLifecycleHandler
{
    public void OnSubjectAttachedToProperty(SubjectLifecycleChange change)
    {
        change.Subject.AddParent(change.Property!.Value, change.Index);
    }

    public void OnSubjectDetachedFromProperty(SubjectLifecycleChange change)
    {
        change.Subject.RemoveParent(change.Property!.Value, change.Index);
    }
}
