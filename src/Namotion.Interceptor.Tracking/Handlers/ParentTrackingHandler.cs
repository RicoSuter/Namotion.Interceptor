using Namotion.Interception.Lifecycle.Abstractions;

namespace Namotion.Interception.Lifecycle.Handlers;

public class ParentTrackingHandler : ILifecycleHandler
{
    public void AddChild(LifecycleContext context)
    {
        if (context.Property != default)
        {
            context.Subject.AddParent(context.Property, context.Index);
        }
    }

    public void RemoveChild(LifecycleContext context)
    {
        if (context.Property != default)
        {
            context.Subject.RemoveParent(context.Property, context.Index);
        }
    }
}
