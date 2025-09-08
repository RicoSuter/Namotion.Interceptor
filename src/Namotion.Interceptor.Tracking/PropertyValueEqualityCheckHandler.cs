namespace Namotion.Interceptor.Tracking;

public class PropertyValueEqualityCheckHandler : IWriteInterceptor
{
    public void WriteProperty<TProperty>(ref WritePropertyInterception<TProperty> context, WriteInterceptionAction<TProperty> next)
    {
        if (typeof(TProperty).IsValueType || typeof(TProperty) == typeof(string))
        {
            if (!EqualityComparer<TProperty>.Default.Equals(context.CurrentValue, context.NewValue))
            {
                next(ref context);
            }
        }
        else
        {
            next(ref context);
        }
    }
}
