using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Tracking.Parent;

public class ParentTrackingHandler : ILifecycleHandler
{
    public void Attach(SubjectLifecycleUpdate update)
    {
        if (update.Property.HasValue)
        {
            update.Subject.AddParent(update.Property.Value, update.Index);
        }
    }

    public void Detach(SubjectLifecycleUpdate update)
    {
        if (update.Property.HasValue)
        {
            update.Subject.RemoveParent(update.Property.Value, update.Index);
        }
    }
}
