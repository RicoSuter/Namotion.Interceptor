using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;
using Namotion.Proxy.Abstractions;

namespace Namotion.Proxy;

public class ProxyContextBuilder : IProxyContextBuilder
{
    private readonly ServiceCollection _serviceCollection = [];

    public IServiceCollection ServiceCollection => _serviceCollection;

    public ProxyContextBuilder TryAddSingleton<TService, TImplementation>(Func<IProxyContext, TImplementation> handler) 
        where TService : class
        where TImplementation : class, TService
    {
        if (_serviceCollection.Any(p => 
            p.ServiceType == typeof(TService) &&
            p.ImplementationType == typeof(TImplementation)))
        {
            return this;
        }
        
        _serviceCollection.AddSingleton<TService, TImplementation>(sp => handler(sp.GetRequiredService<IProxyContext>()));
        return this; 
    }
    
    public ProxyContextBuilder TryAddInterceptor<TService>(Func<IProxyContext, TService> handler)
        where TService : class, IInterceptor
    {
        if (_serviceCollection.Any(p => p.ServiceType == typeof(TService)))
        {
            return this;
        }
        
        _serviceCollection.AddSingleton(sp => handler(sp.GetRequiredService<IProxyContext>()));

        if (typeof(TService).IsAssignableTo(typeof(IWriteInterceptor)))
        {
            _serviceCollection.AddSingleton<IWriteInterceptor>(sp => (IWriteInterceptor)sp.GetRequiredService<TService>());
        }
        
        if (typeof(TService).IsAssignableTo(typeof(IReadInterceptor)))
        {
            _serviceCollection.AddSingleton<IReadInterceptor>(sp => (IReadInterceptor)sp.GetRequiredService<TService>());
        }
        
        if (typeof(TService).IsAssignableTo(typeof(IInterceptor)))
        {
            _serviceCollection.AddSingleton<IInterceptor>(sp => sp.GetRequiredService<TService>());
        }
        
        return this;
    }

    public ProxyContext Build()
    {
        return new ProxyContext(_serviceCollection);
    }
}
