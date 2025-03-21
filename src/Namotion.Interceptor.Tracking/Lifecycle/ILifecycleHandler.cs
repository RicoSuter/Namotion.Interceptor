namespace Namotion.Interceptor.Tracking.Lifecycle;

public interface ILifecycleHandler
{
    public void Attach(SubjectLifecycleUpdate update);

    public void Detach(SubjectLifecycleUpdate update);
}
