using Namotion.Proxy.Abstractions;

namespace Namotion.Proxy.Lifecycle;

public class PropertyValueEqualityCheckHandler : IProxyWriteHandler
{
    public void WriteProperty(WriteProxyPropertyContext context, Action<WriteProxyPropertyContext> next)
    {
        var currentValue = context.GetValueBeforeWrite();
        var newValue = context.NewValue;

        if (!Equals(currentValue, newValue))
        {
            next(context);
        }
    }
}
