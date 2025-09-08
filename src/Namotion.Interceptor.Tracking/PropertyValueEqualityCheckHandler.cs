namespace Namotion.Interceptor.Tracking;

public class PropertyValueEqualityCheckHandler : IWriteInterceptor
{
    public void WriteProperty<TProperty>(ref WritePropertyInterception<TProperty> context, WriteInterceptionAction<TProperty> next)
    {
        if (typeof(TProperty).IsValueType)
        {
            if (!EqualityComparer<TProperty>.Default.Equals(context.CurrentValue, context.NewValue))
            {
                next(ref context);
            }
        }
        else
        {
            if (!ReferenceEquals(context.CurrentValue, context.NewValue))
            {
                next(ref context);
            }
        }
    }
}
