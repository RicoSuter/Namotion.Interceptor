namespace Namotion.Interceptor.Interceptors;

public class InterceptorExecutor : InterceptorSubjectContext
{
    private readonly IInterceptorSubject _subject;

    public InterceptorExecutor(IInterceptorSubject subject)
    {
        _subject = subject;
    }

    public object? InvokeMethod(string methodName, object?[] parameters, Func<object?[], object?> invokeMethod)
    {
        var methodInterceptors = _subject.Context.GetServices<IMethodInterceptor>();

        var returnInvokeMethod = new InvokeMethodInterceptionDelegate((ref context) => invokeMethod(context.Parameters));
        foreach (var handler in methodInterceptors)
        {
            var previousInvokeMethod = returnInvokeMethod;
            returnInvokeMethod = (ref innerContext) =>
            {
                return handler.InvokeMethod(innerContext,
                    (ref innerInnerContext) => previousInvokeMethod(ref innerInnerContext));
            };
        }

        var context = new MethodInvocationContext(_subject, methodName, parameters); 
        return returnInvokeMethod(ref context);
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