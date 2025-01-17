using Microsoft.Extensions.DependencyInjection;
using Namotion.Interception.Lifecycle;
using Namotion.Interception.Lifecycle.Abstractions;

namespace Namotion.Proxy;

public static class ObservableServiceProviderExtensions
{
    public static IObservable<PropertyChangedContext> GetPropertyChangedObservable(this IServiceProvider serviceProvider)
    {
        return serviceProvider.GetRequiredService<PropertyChangedObservable>();
    }
}
