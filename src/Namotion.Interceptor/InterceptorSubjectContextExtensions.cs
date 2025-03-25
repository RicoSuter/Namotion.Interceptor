namespace Namotion.Interceptor;

public static class InterceptorSubjectContextExtensions
{
    public static IInterceptorSubjectContext WithInterceptor<TService>(this IInterceptorSubjectContext context, Func<TService> factory)
        where TService : IInterceptor
    {
        context.TryAddService(factory, _ => true);
        return context;
    }

    public static IInterceptorSubjectContext WithService<TService>(this IInterceptorSubjectContext context, Func<TService> factory)
    {
        context.TryAddService(factory, _ => true);
        return context;
    }

    public static IInterceptorSubjectContext WithService<TService>(this IInterceptorSubjectContext context,
        Func<TService> factory, Func<TService, bool> exists)
    {
        context.TryAddService(factory, exists);
        return context;
    }

    public static TService GetService<TService>(this IInterceptorSubjectContext context)
    {
        return context.TryGetService<TService>() ?? throw new InvalidOperationException("Service not found.");
    }
}