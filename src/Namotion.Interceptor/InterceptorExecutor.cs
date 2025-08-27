namespace Namotion.Interceptor;

public class InterceptorExecutor : InterceptorSubjectContext, IInterceptorExecutor
{
    private readonly IInterceptorSubject _subject;

    public InterceptorExecutor(IInterceptorSubject subject)
    {
        _subject = subject;
    }
    
    public TProperty GetPropertyValue<TProperty>(string propertyName, Func<IInterceptorSubject, TProperty> readValue)
    {
        var interception = new ReadPropertyInterception(new PropertyReference(_subject, propertyName));
        return _subject.Context.ExecuteInterceptedRead(interception, readValue);
    }
    
    public void SetPropertyValue<TProperty>(string propertyName, TProperty newValue, Func<IInterceptorSubject, TProperty>? readValue, Action<IInterceptorSubject, TProperty> writeValue)
    {
        // TODO(perf): Reading current value (invoke getter) here might be a performance problem. 

        var interception = new WritePropertyInterception<TProperty>(
            new PropertyReference(_subject, propertyName), 
            readValue is not null ? readValue(_subject) : default!, 
            newValue); 

        _subject.Context.ExecuteInterceptedWrite(interception, writeValue);
    }

    public object? InvokeMethod(string methodName, object?[] parameters, Func<object?[], object?> invokeMethod)
    {
        var methodInterceptors = _subject.Context.GetServices<IMethodInterceptor>();
        var interception = new MethodInvocationInterception(_subject, methodName, parameters);

        var returnInvokeMethod = new Func<MethodInvocationInterception, object?>(context => invokeMethod(context.Parameters));
    
        foreach (var handler in methodInterceptors)
        {
            var previousInvokeMethod = returnInvokeMethod;
            returnInvokeMethod = (context) =>
            {
                return handler.InvokeMethod(context,
                    innerContext => previousInvokeMethod(innerContext));
            };
        }

        return returnInvokeMethod(interception);
    }

    public override bool AddFallbackContext(IInterceptorSubjectContext context)
    {
        var result = base.AddFallbackContext(context);
        if (result)
        {
            foreach (var interceptor in context.GetServices<IInterceptor>())
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
            foreach (var interceptor in context.GetServices<IInterceptor>())
            {
                interceptor.DetachFrom(_subject);
            }

            return base.RemoveFallbackContext(context);
        }

        return false;
    }
}