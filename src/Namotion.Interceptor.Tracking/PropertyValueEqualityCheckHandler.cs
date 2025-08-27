namespace Namotion.Interceptor.Tracking;

public class PropertyValueEqualityCheckHandler : IWriteInterceptor
{
    public TProperty WriteProperty<TProperty>(WritePropertyInterception<TProperty> context, Func<WritePropertyInterception<TProperty>, TProperty> next)
    {
        if (!EqualityComparer<TProperty>.Default.Equals(context.CurrentValue, context.NewValue))
        {
            return next(context);
        }

        return context.NewValue;
    }
    
    public void AttachTo(IInterceptorSubject subject)
    {
    }

    public void DetachFrom(IInterceptorSubject subject)
    {
    }
}
