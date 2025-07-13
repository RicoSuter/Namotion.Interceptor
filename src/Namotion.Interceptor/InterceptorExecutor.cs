namespace Namotion.Interceptor;

public class InterceptorExecutor : InterceptorSubjectContext, IInterceptorExecutor
{
    private readonly IInterceptorSubject _subject;

    public InterceptorExecutor(IInterceptorSubject subject)
    {
        _subject = subject;
    }
    
    public object? GetPropertyValue(string propertyName, Func<object?> readValue)
    {
        var interception = new ReadPropertyInterception(new PropertyReference(_subject, propertyName));
        return _subject.Context.ExecuteInterceptedRead(interception, readValue);
    }
    
    public void SetPropertyValue(string propertyName, object? newValue, Func<object?>? readValue, Action<object?> writeValue)
    {
        // TODO: reading here might be a problem (performance?)
        var interception = new WritePropertyInterception(
            new PropertyReference(_subject, propertyName), readValue?.Invoke(), newValue); 
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