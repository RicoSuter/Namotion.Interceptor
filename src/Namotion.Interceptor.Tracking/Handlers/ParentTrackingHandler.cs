namespace Namotion.Interceptor.Tracking.Handlers;

public class ParentTrackingHandler : IWriteInterceptor
{
    public object? WriteProperty(WritePropertyInterception context, Func<WritePropertyInterception, object?> next)
    {
        // TODO: Handle list and dictionary
        // TODO: Only remove parent when no other property references it
        
        if (context.CurrentValue is IInterceptorSubject removedSubject)
        {
            removedSubject.RemoveParent(context.Property, null);
        }

        var result = next(context);
        
        if (context.NewValue is IInterceptorSubject addedSubject)
        {
            addedSubject.AddParent(context.Property, null);
        }
        
        return result;
    }
}
