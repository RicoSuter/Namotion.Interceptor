namespace Namotion.Interceptor.Tracking;

public class PropertyValueEqualityCheckHandler : IWriteInterceptor
{
    public object? WriteProperty(WritePropertyInterception context, Func<WritePropertyInterception, object?> next)
    {
        if (!Equals(context.CurrentValue, context.NewValue))
        {
            return next(context);
        }

        return context.NewValue;
    }
}
