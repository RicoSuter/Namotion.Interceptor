using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;

namespace Namotion.Proxy.Abstractions
{
    public interface IInterceptorContextBuilder
    {
        InterceptorContextBuilder TryAddInterceptor<T>(Func<IInterceptorCollection, IServiceProvider, T> handler)
            where T : class, IInterceptor;

        InterceptorContextBuilder TryAddSingleton<TService, TImplementation>(Func<IInterceptorCollection, IServiceProvider, TImplementation> handler)
            where TService : class
            where TImplementation : class, TService;
        
        IServiceCollection ServiceCollection { get; }
        
        IInterceptorContext Build();
    }
}