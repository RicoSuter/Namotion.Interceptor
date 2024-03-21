using Namotion.Proxy.Abstractions;

namespace Namotion.Proxy.Handlers;

internal class PropertyChangedHandler : IProxyWriteHandler
{
    public void SetProperty(ProxyWriteHandlerContext context, Action<ProxyWriteHandlerContext> next)
    {
        var currentValue = context.GetValueBeforeWrite();
        var newValue = context.NewValue;

        next(context);

        var changedContext = new ProxyChangedHandlerContext(context.Context, context.Proxy, context.PropertyName, currentValue, newValue);
        foreach (var handler in context.Context.GetHandlers<IProxyChangedHandler>())
        {
            handler.RaisePropertyChanged(changedContext);
        }
    }
}
