using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;
using Namotion.Proxy.Abstractions;

namespace Namotion.Proxy;

public class ProxyContext : InterceptorCollection, IProxyContext
{
    private readonly IServiceProvider _serviceProvider;

    public static ProxyContextBuilder CreateBuilder()
    {
        return new ProxyContextBuilder();
    }
    
    public ProxyContext(ServiceCollection serviceCollection)
    {
        serviceCollection.AddSingleton<IProxyContext>(this);

        _serviceProvider = serviceCollection.BuildServiceProvider();
        AddInterceptors(_serviceProvider.GetServices<IInterceptor>().Reverse());
    }

    public object? GetService(Type serviceType)
    {
        return _serviceProvider.GetService(serviceType);
    }
}
