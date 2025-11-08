namespace Namotion.Interceptor.Interceptors;

public class InterceptorExecutor : InterceptorSubjectContext
{
    private readonly IInterceptorSubject _subject;

    public InterceptorExecutor(IInterceptorSubject subject)
    {
        _subject = subject;
    }

    public override bool AddFallbackContext(IInterceptorSubjectContext context)
    {
        var result = base.AddFallbackContext(context);
        if (result)
        {
            foreach (var interceptor in context.GetServices<ILifecycleInterceptor>())
            {
                interceptor.AttachTo(_subject);
            }
        }

        return result;
    }

    public override bool RemoveFallbackContext(IInterceptorSubjectContext context)
    {
        if (HasFallbackContext(context))
        {
            foreach (var interceptor in context.GetServices<ILifecycleInterceptor>())
            {
                interceptor.DetachFrom(_subject);
            }

            return base.RemoveFallbackContext(context);
        }

        return false;
    }
}