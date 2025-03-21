namespace Namotion.Interceptor.Sources;

public interface ISubjectSourceManager
{
    void EnqueueSubjectUpdate(Action update);
}