using Microsoft.Extensions.DependencyInjection;
using Namotion.Interception.Lifecycle;
using Namotion.Interception.Lifecycle.Abstractions;
using Namotion.Proxy.Abstractions;

namespace Namotion.Proxy;

public static class ProxyContextExtensions
{
    public static IObservable<ProxyPropertyChanged> GetPropertyChangedObservable(this IProxyContext context)
    {
        return context.GetRequiredService<PropertyChangedObservable>();
    }
}
