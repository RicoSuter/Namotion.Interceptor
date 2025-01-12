using Namotion.Interceptor;
using Namotion.Proxy.Abstractions;

namespace Namotion.Proxy.Lifecycle;

public class PropertyValueEqualityCheckHandler : IWriteInterceptor
{
    public void WriteProperty(WritePropertyInterception context, Action<WritePropertyInterception> next)
    {
        if (!Equals(context.CurrentValue, context.NewValue))
        {
            next(context);
        }
    }
}
