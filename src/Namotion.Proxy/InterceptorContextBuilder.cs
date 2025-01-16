using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;
using Namotion.Proxy.Abstractions;

namespace Namotion.Proxy;

public class InterceptorContextBuilder : IInterceptorContextBuilder
{
    private readonly ServiceCollection _serviceCollection = [];

    public IServiceCollection ServiceCollection => _serviceCollection;

    public InterceptorContextBuilder TryAddSingleton<TService, TImplementation>(Func<IInterceptorCollection, IServiceProvider, TImplementation> handler) 
        where TService : class
        where TImplementation : class, TService
    {
        if (_serviceCollection.Any(p => 
            p.ServiceType == typeof(TService) &&
            p.ImplementationType == typeof(TImplementation)))
        {
            return this;
        }
        
        _serviceCollection.AddSingleton<TService, TImplementation>(sp => 
            handler(
                sp.GetRequiredService<IInterceptorContext>(),
                sp.GetRequiredService<IServiceProvider>()));
      
        return this; 
    }
    
    public InterceptorContextBuilder TryAddInterceptor<TService>(Func<IInterceptorCollection, IServiceProvider, TService> handler)
        where TService : class, IInterceptor
    {
        if (_serviceCollection.Any(p => p.ServiceType == typeof(TService)))
        {
            return this;
        }
        
        _serviceCollection.AddSingleton(sp => handler(
            sp.GetRequiredService<IInterceptorContext>(),
            sp.GetRequiredService<IServiceProvider>()));

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

    public IInterceptorContext Build()
    {
        return new InterceptorContext(_serviceCollection);
    }
}
