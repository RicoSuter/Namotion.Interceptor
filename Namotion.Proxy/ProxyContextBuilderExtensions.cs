using Namotion.Proxy.Abstractions;
using Namotion.Proxy.Handlers;

namespace Namotion.Proxy;

public static class ProxyContextBuilderExtensions
{
    public static IProxyContextBuilder WithFullPropertyTracking(this IProxyContextBuilder builder)
    {
        return builder
            .WithPropertyValueEqualityCheck()
            .WithAutomaticContextAssignment()
            .WithParents()
            .WithDerivedPropertyChangeDetection(initiallyReadAllProperties: true);
    }

    public static IProxyContextBuilder WithPropertyValueEqualityCheck(this IProxyContextBuilder builder)
    {
        return builder
            .TryAddSingleHandler(new PropertyValueEqualityCheckHandler());
    }

    public static IProxyContextBuilder WithAutomaticContextAssignment(this IProxyContextBuilder builder)
    {
        return builder
            .TryAddSingleHandler(new AutomaticContextAssignmentHandler())
            .WithPropertyRegistryHandlers();
    }

    public static IProxyContextBuilder WithParents(this IProxyContextBuilder builder)
    {
        return builder
            .TryAddSingleHandler(new ParentsHandler())
            .WithPropertyRegistryHandlers();
    }

    public static IProxyContextBuilder WithDerivedPropertyChangeDetection(this IProxyContextBuilder builder, bool initiallyReadAllProperties)
    {
        return builder
            .TryAddSingleHandler(new DerivedPropertyChangeDetectionHandler(initiallyReadAllProperties))
            .WithPropertyChangedHandlers();
    }

    public static IProxyContextBuilder WithPropertyChangedCallback(this IProxyContextBuilder builder, Action<ProxyChangedHandlerContext> callback)
    {
        return builder
            .AddHandler(new PropertyChangedCallbackHandler(callback));
    }

    /// <summary>
    /// Adds support for <see cref="IProxyChangedHandler"/> handlers.
    /// </summary>
    /// <param name="builder"></param>
    /// <returns></returns>
    public static IProxyContextBuilder WithPropertyChangedHandlers(this IProxyContextBuilder builder)
    {
        return builder
            .TryAddSingleHandler(new PropertyChangedHandlersHandler());
    }

    /// <summary>
    /// Adds support for <see cref="IProxyPropertyRegistryHandler"/> handlers.
    /// </summary>
    /// <param name="builder"></param>
    /// <returns></returns>
    public static IProxyContextBuilder WithPropertyRegistryHandlers(this IProxyContextBuilder builder)
    {
        return builder
            .TryAddSingleHandler(new PropertyRegistryHandlersHandler());
    }
}