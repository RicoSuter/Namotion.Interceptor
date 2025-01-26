namespace Namotion.Interceptor.Testing;

public static class ObjectExtensions
{
    public static IEnumerable<TInterface> GetServices<TInterface>(this object obj)
    {
        return ((IInterceptorSubject)obj).Interceptors.GetServices<TInterface>();
    }
}