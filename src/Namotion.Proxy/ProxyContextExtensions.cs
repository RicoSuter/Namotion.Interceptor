using Microsoft.Extensions.DependencyInjection;
using Namotion.Proxy.Abstractions;
using Namotion.Proxy.ChangeTracking;

namespace Namotion.Proxy;

public static class ProxyContextExtensions
{
    public static IObservable<ProxyPropertyChanged> GetPropertyChangedObservable(this IProxyContext context)
    {
        return context.GetRequiredService<PropertyChangedObservable>();
    }
}
