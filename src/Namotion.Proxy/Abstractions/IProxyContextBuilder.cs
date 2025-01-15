using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;

namespace Namotion.Proxy.Abstractions
{
    public interface IProxyContextBuilder
    {
        ProxyContextBuilder TryAddInterceptor<T>(Func<IProxyContext, T> handler)
            where T : class, IInterceptor;

        ProxyContextBuilder TryAddSingleton<TService, TImplementation>(Func<IProxyContext, TImplementation> handler)
            where TService : class
            where TImplementation : class, TService;
        
        IServiceCollection ServiceCollection { get; }
        
        ProxyContext Build();
    }
}