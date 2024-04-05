using Namotion.Proxy.Abstractions;

namespace Namotion.Proxy.Lifecycle;

public class PropertyValueEqualityCheckHandler : IProxyWriteHandler
{
    public void WriteProperty(WriteProxyPropertyContext context, Action<WriteProxyPropertyContext> next)
    {
        if (!Equals(context.CurrentValue, context.NewValue))
        {
            next(context);
        }
    }
}
