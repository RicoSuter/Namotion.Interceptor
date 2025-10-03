namespace Namotion.Interceptor.Interceptors;

public class InterceptorExecutor : InterceptorSubjectContext, IInterceptorExecutor
{
    private readonly IInterceptorSubject _subject;

    public InterceptorExecutor(IInterceptorSubject subject)
    {
        _subject = subject;
    }
    
    public TProperty GetPropertyValue<TProperty>(string propertyName, Func<IInterceptorSubject, TProperty> readValue)
    {
        var context = new PropertyReadContext(new PropertyReference(_subject, propertyName));
        return _subject.Context.ExecuteInterceptedRead(ref context, readValue);
    }
    
    public void SetPropertyValue<TProperty>(string propertyName, TProperty newValue, Func<IInterceptorSubject, TProperty>? readValue, Action<IInterceptorSubject, TProperty> writeValue)
    {
        // TODO(perf): Reading current value (invoke getter) here might be a performance problem. 

        var context = new PropertyWriteContext<TProperty>(
            new PropertyReference(_subject, propertyName), 
            readValue is not null ? readValue(_subject) : default!, 
            newValue); 

        _subject.Context.ExecuteInterceptedWrite(ref context, writeValue);
    }

    public object? InvokeMethod(string methodName, object?[] parameters, Func<IInterceptorSubject, object?[], object?> invokeMethod)
    {
        var methodInterceptors = _subject.Context.GetServices<IMethodInterceptor>();

        var returnInvokeMethod = new InvokeMethodInterceptionDelegate((ref context) => invokeMethod(context.Subject, context.Parameters));
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