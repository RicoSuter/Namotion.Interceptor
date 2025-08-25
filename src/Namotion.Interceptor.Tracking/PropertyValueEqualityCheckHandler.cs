namespace Namotion.Interceptor.Tracking;

public class PropertyValueEqualityCheckHandler : IWriteInterceptor
{
    public TProperty WriteProperty<TProperty>(WritePropertyInterception context, Func<WritePropertyInterception, TProperty> next)
    {
        if (!Equals(context.CurrentValue, context.NewValue))
        {
            return next(context);
        }

        return (TProperty)context.NewValue!;
    }
    
    public void AttachTo(IInterceptorSubject subject)
    {
    }

    public void DetachFrom(IInterceptorSubject subject)
    {
    }
}
