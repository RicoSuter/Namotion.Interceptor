using Namotion.Proxy.Abstractions;

namespace Namotion.Proxy.Handlers;

public class PropertyValueEqualityCheckHandler : IProxyWriteHandler
{
    public void SetProperty(ProxyWriteHandlerContext context, Action<ProxyWriteHandlerContext> next)
    {
        var currentValue = context.GetValueBeforeWrite();
        var newValue = context.NewValue;

        if (!Equals(currentValue, newValue))
        {
            next(context);
        }
    }
}
