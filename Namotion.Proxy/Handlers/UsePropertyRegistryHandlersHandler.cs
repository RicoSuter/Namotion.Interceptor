using Namotion.Proxy.Abstractions;

namespace Namotion.Proxy.Handlers;

internal class UsePropertyRegistryHandlersHandler : IProxyWriteHandler
{
    private const string ReferenceCountKey = $"Namotion.Proxy.Handlers.{nameof(UsePropertyRegistryHandlersHandler)}";

    public void SetProperty(ProxyWriteHandlerContext context, Action<ProxyWriteHandlerContext> next)
    {
        var currentValue = context.GetValueBeforeWrite();
        next(context);
        var newValue = context.NewValue;

        if (!Equals(currentValue, newValue))
        {
            if (currentValue is IProxy removedProxy)
            {
                TryDetachProxy(context, removedProxy);
            }

            if (newValue is IProxy assignedProxy)
            {
                TryAttachProxy(context, assignedProxy);
            }
        }
    }

    private static void TryAttachProxy(ProxyWriteHandlerContext context, IProxy proxy)
    {
        var count = proxy.Data.AddOrUpdate(ReferenceCountKey, 1, (_, count) => (int)count! + 1) as int?;
        if (count == 1)
        {
            foreach (var handler in context.Context.GetHandlers<IProxyPropertyRegistryHandler>())
            {
                handler.AttachProxy(context, proxy);
            }
        }
    }

    private static void TryDetachProxy(ProxyWriteHandlerContext context, IProxy proxy)
    {
        var count = proxy.Data.AddOrUpdate(ReferenceCountKey, -1, (_, count) => (int)count! - 1) as int?;
        if (count == 0)
        {
            foreach (var handler in context.Context.GetHandlers<IProxyPropertyRegistryHandler>())
            {
                handler.DetachProxy(context, proxy);
            }
        }
    }
}