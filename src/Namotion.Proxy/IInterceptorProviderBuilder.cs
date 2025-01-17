using Namotion.Interceptor;

namespace Namotion.Proxy
{
    public interface IInterceptorProviderBuilder
    {
        InterceptorProviderBuilder TryAddInterceptor<T>(Func<IServiceProvider, T> handler)
            where T : class, IInterceptor;

        InterceptorProviderBuilder TryAddSingleton<TService, TImplementation>(Func<IServiceProvider, TImplementation> handler)
            where TService : class
            where TImplementation : class, TService;
        
        InterceptorProvider Build();
    }
}