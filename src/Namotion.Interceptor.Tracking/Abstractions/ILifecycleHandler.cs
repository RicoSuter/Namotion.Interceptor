namespace Namotion.Interception.Lifecycle.Abstractions;

public interface ILifecycleHandler
{
    // TODO: Rename to AddChild/RemoveChild
    
    public void AddChild(LifecycleContext context);

    public void RemoveChild(LifecycleContext context);
}
