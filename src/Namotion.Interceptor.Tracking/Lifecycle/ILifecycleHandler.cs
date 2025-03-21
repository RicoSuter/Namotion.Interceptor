namespace Namotion.Interceptor.Tracking.Lifecycle;

public interface ILifecycleHandler
{
    public void Attach(SubjectLifecycleChange change);

    public void Detach(SubjectLifecycleChange change);
}
