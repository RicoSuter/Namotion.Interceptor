namespace Namotion.Interceptor;

public static class InterceptorCollectionExtensions
{
    public static IInterceptorCollection WithInterceptor<TService>(this IInterceptorCollection collection, Func<TService> factory)
        where TService : IInterceptor
    {
        collection.TryAddService<IInterceptor, TService>(factory);
        return collection;
    }
    
    public static IInterceptorCollection WithService<TInterface, TService>(this IInterceptorCollection collection, Func<TService> factory)
    {
        collection.TryAddService<TInterface, TService>(factory);
        return collection;
    }
    
    public static TService GetService<TService>(this IInterceptorCollection collection)
    {
        return collection.TryGetService<TService>() ?? throw new InvalidOperationException("Service not found.");
    }
}