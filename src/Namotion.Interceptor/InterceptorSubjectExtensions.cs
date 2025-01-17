namespace Namotion.Interceptor;

public static class InterceptorSubjectExtensions
{
    public static void AddInterceptors(this IInterceptorSubject subject, IInterceptorProvider addedInterceptors)
    {
        foreach (var interceptor in addedInterceptors.Interceptors)
        {
            subject.Interceptors.AddInterceptor(interceptor);
        }
    }
    
    public static void RemoveInterceptors(this IInterceptorSubject subject, IInterceptorProvider removedInterceptors)
    {
        foreach (var interceptor in removedInterceptors.Interceptors)
        {
            subject.Interceptors.RemoveInterceptor(interceptor);
        }
    }
}