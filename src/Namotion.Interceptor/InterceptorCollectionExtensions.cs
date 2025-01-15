namespace Namotion.Interceptor;

public static class InterceptorCollectionExtensions
{
    public static void AddInterceptors(this IInterceptorSubject subject, IServiceProvider serviceProvider)
    {
        foreach (var interceptor in (IEnumerable<IInterceptor>)serviceProvider.GetService(typeof(IEnumerable<IInterceptor>))!)
        {
            subject.Interceptor.AddInterceptor(interceptor);
        }
    }
}