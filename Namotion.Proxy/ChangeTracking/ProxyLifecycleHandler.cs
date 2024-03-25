using Namotion.Proxy.Abstractions;
using System.Collections;

namespace Namotion.Proxy.ChangeTracking;

internal class ProxyLifecycleHandler : IProxyWriteHandler, IProxyLifecycleHandler
{
    private const string ReferenceCountKey = "Namotion.Proxy.Handlers.ReferenceCount";

    public void OnProxyAttached(ProxyLifecycleContext context)
    {
        foreach (var child in FindProxiesInProperties(context.Proxy, new HashSet<IProxy>()))
        {
            AttachProxy(context.Context, child.Item1, child.Item2, child.Item3);
        }
    }

    public void OnProxyDetached(ProxyLifecycleContext context)
    {
        //foreach (var child in FindProxiesInProperties(context.Proxy, new HashSet<IProxy>()))
        //{
        //    DetachProxy(context.Context, child.Item1, child.Item2, child.Item3);
        //}

        //DetachProxy(context.Context, context.Property, context., child.Item3);
    }

    public void SetProperty(WriteProxyPropertyContext context, Action<WriteProxyPropertyContext> next)
    {
        var currentValue = context.GetValueBeforeWrite();
        next(context);
        var newValue = context.NewValue;

        if (!Equals(currentValue, newValue))
        {
            var oldProxies = FindProxies(context.Property, currentValue, null, new HashSet<IProxy>()).ToDictionary(p => p.Item2, p => p);
            var newProxies = FindProxies(context.Property, newValue, null, new HashSet<IProxy>()).ToDictionary(p => p.Item2, p => p);

            if (oldProxies.Count != 0 || newProxies.Count != 0)
            {
                foreach (var d in oldProxies
                    .Where(u => !newProxies.ContainsKey(u.Key)))
                {
                    DetachProxy(context.Context, d.Value.Item1, d.Value.Item2, d.Value.Item3);
                }

                foreach (var d in newProxies
                    .Where(u => !oldProxies.ContainsKey(u.Key)))
                {
                    AttachProxy(context.Context, d.Value.Item1, d.Value.Item2, d.Value.Item3);
                }
            }
        }
    }

    private void AttachProxy(IProxyContext context, ProxyPropertyReference property, IProxy proxy, object? index)
    {
        var count = proxy.Data.AddOrUpdate(ReferenceCountKey, 1, (_, count) => (int)count! + 1) as int?;
        var registryContext = new ProxyLifecycleContext(property, index, proxy, count ?? 1, context);

        foreach (var handler in context.GetHandlers<IProxyLifecycleHandler>())
        {
            if (handler != this)
            {
                handler.OnProxyAttached(registryContext);
            }
        }
    }

    private void DetachProxy(IProxyContext context, ProxyPropertyReference property, IProxy proxy, object? index)
    {
        var count = proxy.Data.AddOrUpdate(ReferenceCountKey, 0, (_, count) => (int)count! - 1) as int?;
        var registryContext = new ProxyLifecycleContext(property, index, proxy, count ?? 1, context);
        foreach (var handler in context.GetHandlers<IProxyLifecycleHandler>())
        {
            if (handler != this)
            {
                handler.OnProxyDetached(registryContext);
            }
        }
    }

    private IEnumerable<(ProxyPropertyReference, IProxy, object?)> FindProxies(
        ProxyPropertyReference property,
        object? value, object? index, 
        HashSet<IProxy> seen)
    {
        if (value is IDictionary dictionary)
        {
            foreach (var key in dictionary.Keys)
            {
                var dictionaryValue = dictionary[key];
                if (dictionaryValue is IProxy proxy)
                {
                    foreach (var child in FindProxies(property, proxy, key, seen))
                    {
                        yield return child;
                    }
                }
            }
        }
        else if (value is ICollection collection)
        {
            var i = 0;
            foreach (var proxy in collection.OfType<IProxy>())
            {
                foreach (var child in FindProxies(property, proxy, i, seen))
                {
                    yield return child;
                }
                i++;
            }
        }
        else if (value is IProxy proxy)
        {
            if (!seen.Contains(proxy))
            {
                seen.Add(proxy);

                yield return (property, proxy, index);

                foreach (var child in FindProxiesInProperties(proxy, seen))
                {
                    yield return child;
                }
            }
        }
    }

    private IEnumerable<(ProxyPropertyReference, IProxy, object?)> FindProxiesInProperties(IProxy proxy, HashSet<IProxy> seen)
    {
        foreach (var property in proxy.Properties)
        {
            var childValue = property.Value.GetValue?.Invoke(proxy);
            foreach (var child in FindProxies(new ProxyPropertyReference(proxy, property.Key), childValue, null, seen))
            {
                yield return child;
            }
        }
    }
}