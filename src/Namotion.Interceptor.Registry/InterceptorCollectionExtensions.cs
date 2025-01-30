using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Registry;

public static class InterceptorCollectionExtensions
{
    /// <summary>
    /// Adds support for <see cref="ILifecycleHandler"/> handlers.
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <returns>The builder.</returns>
    public static IInterceptorCollection WithRegistry(this IInterceptorCollection builder)
    {
        builder
            .TryAddService(() => new ProxyRegistry(), _ => true);

        return builder
            .WithInterceptorInheritance();
    }
}