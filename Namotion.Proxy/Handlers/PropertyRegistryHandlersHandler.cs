using Namotion.Proxy.Abstractions;
using System.Collections;

namespace Namotion.Proxy.Handlers;

internal class PropertyRegistryHandlersHandler : IProxyWriteHandler
{
    private const string ReferenceCountKey = "Namotion.Proxy.Handlers.ReferenceCount";

    public void SetProperty(ProxyWriteHandlerContext context, Action<ProxyWriteHandlerContext> next)
    {
        var currentValue = context.GetValueBeforeWrite();
        next(context);
        var newValue = context.NewValue;

        if (!Equals(currentValue, newValue))
        {
            TryDetachProxy(context.Context, context.Proxy, context.PropertyName, currentValue, null);
            TryAttachProxy(context.Context, context.Proxy, context.PropertyName, newValue, null);
        }
    }

    private static void TryAttachProxy(IProxyContext context, IProxy parentProxy, string propertyName, object? value, object? index)
    {
        if (value is IDictionary dictionary)
        {
            foreach (var key in dictionary.Keys)
            {
                var dictionaryValue = dictionary[key];
                if (dictionaryValue is IProxy proxy)
                {
                    TryAttachProxy(context, parentProxy, propertyName, proxy, key);
                }
            }
        }
        else if (value is ICollection collection)
        {
            var i = 0;
            foreach (var proxy in collection.OfType<IProxy>())
            {
                TryAttachProxy(context, parentProxy, propertyName, proxy, i);
                i++;
            }
        }
        else if (value is IProxy proxy)
        {
            var count = proxy.Data.AddOrUpdate(ReferenceCountKey, 1, (_, count) => (int)count! + 1) as int?;
            var registryContext = new ProxyPropertyRegistryHandlerContext(context, parentProxy, propertyName, index, proxy, count ?? 1);

            foreach (var handler in context.GetHandlers<IProxyPropertyRegistryHandler>())
            {
                handler.AttachProxy(registryContext, proxy);
            }

            // TODO: Avoid infinite recursion when circular references are present
            foreach (var property in proxy.Properties)
            {
                var childValue = property.Value.ReadValue(proxy);
                TryAttachProxy(context, proxy, property.Key, childValue, null);
            }
        }
    }

    private static void TryDetachProxy(IProxyContext context, IProxy parentProxy, string propertyName, object? value, object? index)
    {
        if (value is IDictionary dictionary)
        {
            foreach (var key in dictionary.Keys)
            {
                var dictionaryValue = dictionary[key];
                if (dictionaryValue is IProxy proxy)
                {
                    TryDetachProxy(context, parentProxy, propertyName, proxy, key);
                }
            }
        }
        else if (value is ICollection collection)
        {
            var i = 0;
            foreach (var proxy in collection.OfType<IProxy>())
            {
                TryDetachProxy(context, parentProxy, propertyName, proxy, i);
                i++;
            }
        }
        else if (value is IProxy proxy)
        {
            // TODO: Avoid infinite recursion when circular references are present
            foreach (var property in proxy.Properties)
            {
                var childValue = property.Value.ReadValue(proxy);
                TryDetachProxy(context, proxy, property.Key, childValue, null);
            }

            var count = proxy.Data.AddOrUpdate(ReferenceCountKey, -1, (_, count) => (int)count! - 1) as int?;
            var registryContext = new ProxyPropertyRegistryHandlerContext(context, parentProxy, propertyName, index, proxy, count ?? 1);
            foreach (var handler in context.GetHandlers<IProxyPropertyRegistryHandler>())
            {
                handler.DetachProxy(registryContext, proxy);
            }
        }
    }
}