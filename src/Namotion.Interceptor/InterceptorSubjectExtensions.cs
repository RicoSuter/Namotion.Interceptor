namespace Namotion.Interceptor;

public static class InterceptorSubjectExtensions
{
    public static void AddInterceptors(this IInterceptorSubject subject, IInterceptorProvider addedInterceptors)
    {
        subject.Interceptors.AddInterceptors(addedInterceptors.Interceptors);
    }
    
    public static void RemoveInterceptors(this IInterceptorSubject subject, IInterceptorProvider removedInterceptors)
    {
        subject.Interceptors.RemoveInterceptors(removedInterceptors.Interceptors);
    }
}