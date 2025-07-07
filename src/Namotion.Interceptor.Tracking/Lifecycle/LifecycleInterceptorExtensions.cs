namespace Namotion.Interceptor.Tracking.Lifecycle;

public static class LifecycleInterceptorExtensions
{
    public static void AttachSubjectProperty(this IInterceptorSubject subject, PropertyReference property)
    {            
        var change = new SubjectPropertyLifecycleChange(subject, property);

        foreach (var handler in subject.Context.GetServices<ISubjectPropertyLifecycleHandler>())
        {
            handler.AttachSubjectProperty(change);
        }

        if (subject is ISubjectPropertyLifecycleHandler lifecycleHandler)
        {
            lifecycleHandler.AttachSubjectProperty(change);
        }
    }
    
    public static void DetachSubjectProperty(this IInterceptorSubject subject, PropertyReference property)
    {            
        var change = new SubjectPropertyLifecycleChange(subject, property);

        foreach (var handler in subject.Context.GetServices<ISubjectPropertyLifecycleHandler>())
        {
            handler.DetachSubjectProperty(change);
        }

        if (subject is ISubjectPropertyLifecycleHandler lifecycleHandler)
        {
            lifecycleHandler.DetachSubjectProperty(change);
        }
    }
}