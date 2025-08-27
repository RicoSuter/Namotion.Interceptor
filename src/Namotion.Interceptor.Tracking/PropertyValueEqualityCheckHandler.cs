namespace Namotion.Interceptor.Tracking;

public class PropertyValueEqualityCheckHandler : IWriteInterceptor
{
    public void WriteProperty<TProperty>(WritePropertyInterception<TProperty> context, Action<WritePropertyInterception<TProperty>> next)
    {
        if (!EqualityComparer<TProperty>.Default.Equals(context.CurrentValue, context.NewValue))
        {
            next(context);
        }
    }
    
    public void AttachTo(IInterceptorSubject subject)
    {
    }

    public void DetachFrom(IInterceptorSubject subject)
    {
    }
}
