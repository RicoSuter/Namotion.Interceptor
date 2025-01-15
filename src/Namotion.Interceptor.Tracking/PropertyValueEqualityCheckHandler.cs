using Namotion.Interceptor;

namespace Namotion.Interception.Lifecycle;

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
