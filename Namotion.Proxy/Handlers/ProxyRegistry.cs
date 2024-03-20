using Namotion.Proxy.Abstractions;
using System.Collections;

namespace Namotion.Proxy.Handlers;

public static class ProxyRegistryExtensions
{

}

internal class ProxyRegistry : IProxyWriteHandler, IProxyRegistry, IProxyPropertyRegistryHandler
{
    private HashSet<IProxy> _knownProxies = new HashSet<IProxy>();

    public IEnumerable<IProxy> KnownProxies
    {
        get
        {
            lock (_knownProxies)
                return _knownProxies.ToArray();
        }
    }

    private const string ReferenceCountKey = "Namotion.Proxy.Handlers.ReferenceCount";

    public void SetProperty(ProxyWriteHandlerContext context, Action<ProxyWriteHandlerContext> next)
    {
        var currentValue = context.GetValueBeforeWrite();
        next(context);
        var newValue = context.NewValue;

        if (!Equals(currentValue, newValue))
        {
            // TODO: Write unit tests for this!
            var oldProxies = FindProxies(context.Proxy, context.PropertyName, currentValue, null, true).ToDictionary(p => p.Item3, p => p);
            var newProxies = FindProxies(context.Proxy, context.PropertyName, newValue, null, true).ToDictionary(p => p.Item3, p => p);

            if (oldProxies.Count != 0 || newProxies.Count != 0)
            {
                lock (_knownProxies)
                {
                    foreach (var d in oldProxies
                        .Where(u => !newProxies.ContainsKey(u.Key)))
                    {
                        DetachProxy(context.Context, d.Value.Item1, d.Value.Item2, d.Value.Item3, d.Value.Item4);
                        _knownProxies.Remove(d.Value.Item3);
                    }

                    foreach (var d in newProxies
                        .Where(u => !oldProxies.ContainsKey(u.Key)))
                    {
                        _knownProxies.Add(d.Value.Item3);
                        AttachProxy(context.Context, d.Value.Item1, d.Value.Item2, d.Value.Item3, d.Value.Item4);
                    }
                }
            }
        }
    }

    private void AttachProxy(IProxyContext context, IProxy parentProxy, string propertyName, IProxy proxy, object? index)
    {
        var count = proxy.Data.AddOrUpdate(ReferenceCountKey, 1, (_, count) => (int)count! + 1) as int?;
        var registryContext = new ProxyPropertyRegistryHandlerContext(context, parentProxy, propertyName, index, proxy, count ?? 1);

        foreach (var handler in context.GetHandlers<IProxyPropertyRegistryHandler>())
        {
            if (handler != this)
            {
                handler.AttachProxy(registryContext, proxy);
            }
        }
    }

    private void DetachProxy(IProxyContext context, IProxy parentProxy, string propertyName, IProxy proxy, object? index)
    {
        var count = proxy.Data.AddOrUpdate(ReferenceCountKey, 0, (_, count) => (int)count! - 1) as int?;
        var registryContext = new ProxyPropertyRegistryHandlerContext(context, parentProxy, propertyName, index, proxy, count ?? 1);
        foreach (var handler in context.GetHandlers<IProxyPropertyRegistryHandler>())
        {
            if (handler != this)
            {
                handler.DetachProxy(registryContext, proxy);
            }
        }
    }

    private IEnumerable<(IProxy, string, IProxy, object?)> FindProxies(IProxy parentProxy, string propertyName, object? value, object? index, bool includeKnown)
    {
        if (value is IDictionary dictionary)
        {
            foreach (var key in dictionary.Keys)
            {
                var dictionaryValue = dictionary[key];
                if (dictionaryValue is IProxy proxy)
                {
                    foreach (var child in FindProxies(parentProxy, propertyName, proxy, key, includeKnown))
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
                foreach (var child in FindProxies(parentProxy, propertyName, proxy, i, includeKnown))
                {
                    yield return child;
                }
                i++;
            }
        }
        else if (value is IProxy proxy)
        {
            if (includeKnown == false)
            {
                lock (_knownProxies)
                {
                    if (_knownProxies.Contains(proxy))
                    {
                        yield break;
                    }
                }
            }

            // TODO: Avoid infinite recursion when circular references are present
            foreach (var property in proxy.Properties)
            {
                var childValue = property.Value.ReadValue(proxy);
                foreach (var child in FindProxies(proxy, property.Key, childValue, null, includeKnown))
                {
                    yield return child;
                }
            }

            yield return (parentProxy, propertyName, proxy, index);
        }
    }

    // needed so that SetContext works

    public void AttachProxy(ProxyPropertyRegistryHandlerContext context, IProxy proxy)
    {
        lock (_knownProxies)
            _knownProxies.Add(proxy);
    }

    public void DetachProxy(ProxyPropertyRegistryHandlerContext context, IProxy proxy)
    {
        lock (_knownProxies)
            _knownProxies.Remove(proxy);
    }
}