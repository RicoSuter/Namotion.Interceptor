using Namotion.Proxy.Abstractions;
using Namotion.Proxy.Handlers;

namespace Namotion.Proxy;

public static class ProxyContextBuilderExtensions
{
    public static IProxyContextBuilder CheckPropertyEqualityBeforeAssignment(this IProxyContextBuilder builder)
    {
        return builder
            .TryAddSingleHandler(new CheckPropertyEqualityBeforeAssignmentHandler());
    }

    public static IProxyContextBuilder AutomaticallyAssignContextToPropertyValues(this IProxyContextBuilder builder)
    {
        return builder
            .UsePropertyRegistryHandlers()
            .TryAddSingleHandler(new AutomaticallyAssignContextToPropertyValuesHandler());
    }

    public static IProxyContextBuilder DetectDerivedPropertyChanges(this IProxyContextBuilder builder, bool initiallyReadAllProperties)
    {
        return builder
            .UsePropertyChangedHandlers()
            .TryAddSingleHandler(new DetectDerivedPropertyChangesHandler(initiallyReadAllProperties));
    }

    /// <summary>
    /// Adds support for <see cref="IProxyChangedHandler"/> handlers.
    /// </summary>
    /// <param name="builder"></param>
    /// <returns></returns>
    public static IProxyContextBuilder UsePropertyChangedHandlers(this IProxyContextBuilder builder)
    {
        return builder
            .TryAddSingleHandler(new UsePropertyChangedHandlersHandler());
    }

    /// <summary>
    /// Adds support for <see cref="IProxyPropertyRegistryHandler"/> handlers.
    /// </summary>
    /// <param name="builder"></param>
    /// <returns></returns>
    public static IProxyContextBuilder UsePropertyRegistryHandlers(this IProxyContextBuilder builder)
    {
        return builder
            .TryAddSingleHandler(new UsePropertyRegistryHandlersHandler());
    }
}