namespace Namotion.Interceptor.Sources;

public interface ISubjectSourceDispatcher
{
    void EnqueueSubjectUpdate(Action update);
}