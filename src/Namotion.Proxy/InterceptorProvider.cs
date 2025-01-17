using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;

namespace Namotion.Proxy;

public class InterceptorProvider : IInterceptorProvider, IServiceProvider
{
    private readonly IServiceProvider _serviceProvider;

    public static InterceptorProviderBuilder CreateBuilder()
    {
        return new InterceptorProviderBuilder();
    }
    
    public InterceptorProvider(IServiceCollection serviceCollection)
    {
        _serviceProvider = serviceCollection.BuildServiceProvider();
    }

    public object? GetService(Type serviceType)
    {
        return _serviceProvider.GetService(serviceType);
    }

    public IEnumerable<IInterceptor> Interceptors => _serviceProvider.GetServices<IInterceptor>();
}
