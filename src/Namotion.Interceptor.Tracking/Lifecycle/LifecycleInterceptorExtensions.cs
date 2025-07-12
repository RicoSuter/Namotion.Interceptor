namespace Namotion.Interceptor.Tracking.Lifecycle;

public static class LifecycleInterceptorExtensions
{
    public static void AttachSubjectProperty(this IInterceptorSubject subject, PropertyReference property)
    {            
        var change = new SubjectPropertyLifecycleChange(subject, property);

        foreach (var handler in subject.Context.GetServices<IPropertyLifecycleHandler>())
        {
            handler.AttachProperty(change);
        }

        if (subject is IPropertyLifecycleHandler lifecycleHandler)
        {
            lifecycleHandler.AttachProperty(change);
        }
    }
    
    public static void DetachSubjectProperty(this IInterceptorSubject subject, PropertyReference property)
    {            
        var change = new SubjectPropertyLifecycleChange(subject, property);

        foreach (var handler in subject.Context.GetServices<IPropertyLifecycleHandler>())
        {
            handler.DetachProperty(change);
        }

        if (subject is IPropertyLifecycleHandler lifecycleHandler)
        {
            lifecycleHandler.DetachProperty(change);
        }
    }
}