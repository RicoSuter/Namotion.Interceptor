namespace Namotion.Interceptor;

public static class InterceptorCollectionExtensions
{
    public static IInterceptorCollection WithInterceptor<TService>(this IInterceptorCollection collection, Func<TService> factory)
        where TService : IInterceptor
    {
        collection.TryAddService(factory, _ => true);
        return collection;
    }
    
    public static IInterceptorCollection WithService<TService>(this IInterceptorCollection collection, Func<TService> factory)
    {
        collection.TryAddService(factory, _ => true);
        return collection;
    }
    
    public static IInterceptorCollection WithService<TService>(this IInterceptorCollection collection, 
        Func<TService> factory, Func<TService, bool> exists)
    {
        collection.TryAddService(factory, exists);
        return collection;
    }
    
    public static TService GetService<TService>(this IInterceptorCollection collection)
    {
        return collection.TryGetService<TService>() ?? throw new InvalidOperationException("Service not found.");
    }
}