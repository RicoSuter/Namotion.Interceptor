using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Tracking.Parent;

public class ParentTrackingHandler : ILifecycleHandler
{
    public void Attach(LifecycleContext context)
    {
        if (context.Property.HasValue)
        {
            context.Subject.AddParent(context.Property.Value, context.Index);
        }
    }

    public void Detach(LifecycleContext context)
    {
        if (context.Property.HasValue)
        {
            context.Subject.RemoveParent(context.Property.Value, context.Index);
        }
    }
}
