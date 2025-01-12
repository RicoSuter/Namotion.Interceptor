using Namotion.Proxy.Abstractions;

using System.Collections;
using Namotion.Interceptor;

namespace Namotion.Proxy.ChangeTracking;

internal class ProxyLifecycleHandler : IWriteInterceptor, IProxyLifecycleHandler
{
    private const string ReferenceCountKey = "Namotion.ReferenceCount";
 
    private readonly Lazy<IProxyLifecycleHandler[]> _handlers;

    public ProxyLifecycleHandler(Lazy<IProxyLifecycleHandler[]> handlers)
    {
        _handlers = handlers;
    }

    // TODO: does it make sense that the two methods are not "the same"?

    public void OnProxyAttached(ProxyLifecycleContext context)
    {
        var touchedProxies = new HashSet<IInterceptorSubject>();
        var proxyProperties = new HashSet<(IInterceptorSubject, PropertyReference, object?)>();
        FindProxiesInProperties(context.Proxy, touchedProxies, proxyProperties);
        
        foreach (var child in proxyProperties)
        {
            AttachProxy(context.Context, child.Item2, child.Item1, child.Item3);
        }
    }

    // TODO: What should we do here?
    public void OnProxyDetached(ProxyLifecycleContext context)
    {
        //foreach (var child in FindProxiesInProperties(context.Interceptable, new HashSet<IInterceptor>()))
        //{
        //    DetachProxy(context.Context, child.Item1, child.Item2, child.Item3);
        //}

        //DetachProxy(context.Context, context.Property, context., child.Item3);
    }

    public void WriteProperty(WritePropertyInterception context, Action<WritePropertyInterception> next)
    {
        var currentValue = context.CurrentValue;
        next(context);
        var newValue = context.NewValue;

        if (!Equals(currentValue, newValue))
        {
            var oldProxies = new HashSet<IInterceptorSubject>();
            var oldProxyProperties = new HashSet<(IInterceptorSubject, PropertyReference, object?)>();
            FindProxies(context.Property, currentValue, null, oldProxies, oldProxyProperties);

            var newProxies = new HashSet<IInterceptorSubject>();
            var newProxyProperties = new HashSet<(IInterceptorSubject, PropertyReference, object?)>();
            FindProxies(context.Property, newValue, null, newProxies, newProxyProperties);

            if (oldProxyProperties.Count != 0 || newProxyProperties.Count != 0)
            {
                foreach (var d in oldProxyProperties
                    .Where(u => !newProxies.Contains(u.Item1)))
                {
                    DetachProxy((IProxyContext)context.Context, d.Item2, d.Item1, d.Item3);
                }

                foreach (var d in newProxyProperties
                    .Where(u => !oldProxies.Contains(u.Item1)))
                {
                    AttachProxy((IProxyContext)context.Context, d.Item2, d.Item1, d.Item3);
                }
            }
        }
    }

    private void AttachProxy(IProxyContext context, PropertyReference property, IInterceptorSubject subject, object? index)
    {
        var count = subject.Data.AddOrUpdate(ReferenceCountKey, 1, (_, count) => (int)count! + 1) as int?;
        var registryContext = new ProxyLifecycleContext(property, index, subject, count ?? 1, context);

        foreach (var handler in _handlers.Value)
        {
            if (handler != this)
            {
                handler.OnProxyAttached(registryContext);
            }
        }
    }

    private void DetachProxy(IProxyContext context, PropertyReference property, IInterceptorSubject subject, object? index)
    {
        var count = subject.Data.AddOrUpdate(ReferenceCountKey, 0, (_, count) => (int)count! - 1) as int?;
        var registryContext = new ProxyLifecycleContext(property, index, subject, count ?? 1, context);
       
        foreach (var handler in _handlers.Value)
        {
            if (handler != this)
            {
                handler.OnProxyDetached(registryContext);
            }
        }
    }

    private void FindProxies(
        PropertyReference property,
        object? value, object? index,
        HashSet<IInterceptorSubject> touchedProxies,
        HashSet<(IInterceptorSubject, PropertyReference, object?)> result)
    {
        if (value is IDictionary dictionary)
        {
            foreach (var key in dictionary.Keys)
            {
                var dictionaryValue = dictionary[key];
                if (dictionaryValue is IInterceptorSubject proxy)
                {
                    FindProxies(property, proxy, key, touchedProxies, result);
                }
            }
        }
        else if (value is ICollection collection)
        {
            var i = 0;
            foreach (var proxy in collection.OfType<IInterceptorSubject>())
            {
                FindProxies(property, proxy, i, touchedProxies, result);
                i++;
            }
        }
        else if (value is IInterceptorSubject proxy)
        {
            result.Add((proxy, property, index));

            if (!touchedProxies.Contains(proxy))
            {
                touchedProxies.Add(proxy);
                FindProxiesInProperties(proxy, touchedProxies, result);
            }
        }
    }

    private void FindProxiesInProperties(IInterceptorSubject subject,
        HashSet<IInterceptorSubject> touchedProxies,
        HashSet<(IInterceptorSubject, PropertyReference, object?)> result)
    {
        foreach (var property in subject.Properties)
        {
            var childValue = property.Value.GetValue?.Invoke(subject);
            if (childValue is not null)
            {
                FindProxies(new PropertyReference(subject, property.Key), childValue, null, touchedProxies, result);
            }
        }
    }
}