namespace Namotion.Interceptor.Tracking.Abstractions;

public interface ILifecycleHandler
{
    public void Attach(LifecycleContext context);

    public void Detach(LifecycleContext context);
}
