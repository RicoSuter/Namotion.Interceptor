using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Abstractions;

namespace Namotion.Interceptor.Validation;

public static class ObservableServiceProviderExtensions
{
    public static IObservable<PropertyChangedContext> GetPropertyChangedObservable(this IInterceptorCollection collection)
    {
        return collection.GetService<PropertyChangedObservable>();
    }
}
