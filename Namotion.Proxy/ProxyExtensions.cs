using Namotion.Proxy.Abstractions;

using System.Collections;

namespace Namotion.Proxy;

public static class ProxyExtensions
{
    /// <summary>
    /// Will attach the proxy and its children to the context and 
    /// detach only the proxy itself from the previous context.
    /// </summary>
    /// <param name="proxy">The proxy.</param>
    /// <param name="context">The context.</param>
    public static void SetContext(this IProxy proxy, IProxyContext? context)
    {
        var currentContext = proxy.Context;
        if (currentContext != context)
        {
            if (currentContext is not null)
            {
                var registryContext = new ProxyLifecycleContext(default, null, proxy, 0, currentContext);
                foreach (var handler in currentContext.GetHandlers<IProxyLifecycleHandler>())
                {
                    handler.OnProxyDetached(registryContext);
                }
            }

            proxy.Context = context;

            if (context is not null)
            {
                var registryContext = new ProxyLifecycleContext(default, null, proxy, 1, context);
                foreach (var handler in context.GetHandlers<IProxyLifecycleHandler>())
                {
                    handler.OnProxyAttached(registryContext);
                }
            }
        }
    }

    public static void SetData(this IProxy proxy, string key, object? value)
    {
        proxy.Data[key] = value;
    }

    public static bool TryGetData(this IProxy proxy, string key, out object? value)
    {
        return proxy.Data.TryGetValue(key, out value);
    }

    public static (IProxy?, PropertyMetadata) FindPropertyFromJsonPath(this IProxy proxy, string path)
    {
        return proxy.FindPropertyFromJsonPath(path.Split('.'));
    }

    private static (IProxy?, PropertyMetadata) FindPropertyFromJsonPath(this IProxy proxy, IEnumerable<string> segments)
    {
        // TODO: Improve this code, correcly apply JSON naming policy from serializer options

        var nextSegment = ToUpperStart(segments.First());
        segments = segments.Skip(1);
    
        if (segments.Any())
        {
            if (nextSegment.Contains('['))
            {
                var arraySegments = nextSegment.Split('[', ']');
                var index = int.Parse(arraySegments[1]);
                nextSegment = arraySegments[0];

                var collection = proxy.Properties[nextSegment].GetValue?.Invoke(proxy) as ICollection;
                var child = collection?.OfType<IProxy>().ElementAt(index);
                return child is not null ? FindPropertyFromJsonPath(child, segments) : (null, default);
            }
            else
            {
                var child = proxy.Properties[nextSegment].GetValue?.Invoke(proxy) as IProxy;
                return child is not null ? FindPropertyFromJsonPath(child, segments) : (null, default);
            }
        }
        else
        {
            return (proxy, proxy.Properties[nextSegment]);
        }
    }

    private static string ToUpperStart(string value)
    {
        return value[0].ToString().ToUpperInvariant() + value[1..];
    }
}
