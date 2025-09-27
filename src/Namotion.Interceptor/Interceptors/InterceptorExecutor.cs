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
        var interception = new PropertyReadContext(new PropertyReference(_subject, propertyName));
        return _subject.Context.ExecuteInterceptedRead(ref interception, readValue);
    }
    
    public void SetPropertyValue<TProperty>(string propertyName, TProperty newValue, Func<IInterceptorSubject, TProperty>? readValue, Action<IInterceptorSubject, TProperty> writeValue)
    {
        // TODO(perf): Reading current value (invoke getter) here might be a performance problem. 

        var interception = new PropertyWriteContext<TProperty>(
            new PropertyReference(_subject, propertyName), 
            readValue is not null ? readValue(_subject) : default!, 
            newValue); 

        _subject.Context.ExecuteInterceptedWrite(ref interception, writeValue);
    }

    public object? InvokeMethod(string methodName, object?[] parameters, Func<object?[], object?> invokeMethod)
    {
        var methodInterceptors = _subject.Context.GetServices<IMethodInterceptor>();

        var returnInvokeMethod = new InvokeMethodInterceptionDelegate((ref context) => invokeMethod(context.Parameters));
        var invocationContext = new MethodInvocationContext(_subject, methodName, parameters); 
        
        foreach (var handler in methodInterceptors)
        {
            var previousInvokeMethod = returnInvokeMethod;
            returnInvokeMethod = (ref context) =>
            {
                return handler.InvokeMethod(context,
                    (ref innerInvocationContext) => previousInvokeMethod(ref innerInvocationContext));
            };
        }

        return returnInvokeMethod(ref invocationContext);
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