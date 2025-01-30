namespace Namotion.Interceptor.Tracking.Lifecycle;

public interface ILifecycleHandler
{
    public void Attach(LifecycleContext context);

    public void Detach(LifecycleContext context);
}
