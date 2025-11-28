using System.Collections.Immutable;

namespace Namotion.Interceptor.Testing;

public static class ObjectExtensions
{
    public static ImmutableArray<TInterface> GetServices<TInterface>(this object obj)
    {
        return ((IInterceptorSubject)obj).Context.GetServices<TInterface>();
    }
}