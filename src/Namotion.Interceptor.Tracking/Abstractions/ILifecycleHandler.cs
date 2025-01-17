namespace Namotion.Interception.Lifecycle.Abstractions;

public interface ILifecycleHandler
{
    public void Attach(LifecycleContext context);

    public void Detach(LifecycleContext context);
}
