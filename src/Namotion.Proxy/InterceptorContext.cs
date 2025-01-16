using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;
using Namotion.Proxy.Abstractions;

namespace Namotion.Proxy;

public class InterceptorContext : IInterceptorContext
{
    private readonly IServiceProvider _serviceProvider;

    public static InterceptorContextBuilder CreateBuilder()
    {
        return new InterceptorContextBuilder();
    }
    
    public InterceptorContext(IServiceCollection serviceCollection)
    {
        serviceCollection.AddSingleton<IInterceptorContext>(this);
        _serviceProvider = serviceCollection.BuildServiceProvider();
    }

    public object? GetService(Type serviceType)
    {
        return _serviceProvider.GetService(serviceType);
    }

    public IEnumerable<IInterceptor> Interceptors => _serviceProvider.GetServices<IInterceptor>();

    public void AddInterceptor(IInterceptor interceptor)
    {
        throw new NotImplementedException();
    }

    public void RemoveInterceptor(IInterceptor interceptor)
    {
        throw new NotImplementedException();
    }

    public object? GetProperty(IInterceptorSubject subject, string propertyName, Func<object?> readValue)
    {
        throw new NotImplementedException();
    }

    public void SetProperty(IInterceptorSubject subject, string propertyName, object? newValue, Func<object?> readValue, Action<object?> writeValue)
    {
        throw new NotImplementedException();
    }
}
