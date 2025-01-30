namespace Namotion.Interceptor.Tracking.Parent;

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

    public void AttachTo(IInterceptorSubject subject)
    {
    }

    public void DetachFrom(IInterceptorSubject subject)
    {
    }
}
