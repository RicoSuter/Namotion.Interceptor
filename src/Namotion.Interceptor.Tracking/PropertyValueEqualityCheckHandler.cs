using System.Runtime.CompilerServices;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tracking;

public class PropertyValueEqualityCheckHandler : IWriteInterceptor
{
    public bool ShouldInterceptWrite
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
    {
        if (!EqualityComparer<TProperty>.Default.Equals(context.CurrentValue, context.NewValue))
        {
            next(ref context);
        }
    }
}
