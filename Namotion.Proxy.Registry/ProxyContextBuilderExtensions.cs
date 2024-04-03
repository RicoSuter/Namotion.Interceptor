using Namotion.Proxy.Abstractions;
using Namotion.Proxy.ChangeTracking;
using Namotion.Proxy.Lifecycle;
using Namotion.Proxy.Registry;
using Namotion.Proxy.Validation;

namespace Namotion.Proxy;

public static class ProxyContextBuilderExtensions
{
    /// <summary>
    /// Adds support for <see cref="IProxyLifecycleHandler"/> handlers.
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <returns>The builder.</returns>
    public static IProxyContextBuilder WithRegistry(this IProxyContextBuilder builder)
    {
        return builder
            .TryAddSingleHandler(new ProxyRegistry())
            .WithAutomaticContextAssignment();
    }
}