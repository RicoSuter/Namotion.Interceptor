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
        var readInterceptors = _subject.Context.GetServices<IReadInterceptor>();
        var interception = new ReadPropertyInterception(new PropertyReference(_subject, propertyName));
        
        var returnReadValue = new Func<ReadPropertyInterception, object?>(_ => readValue());
    
        foreach (var handler in readInterceptors)
        {
            var previousReadValue = returnReadValue;
            returnReadValue = context =>
                handler.ReadProperty(context, ctx => previousReadValue(ctx));
        }

        return returnReadValue(interception);
    }
    
    public void SetPropertyValue(string propertyName, object? newValue, Func<object?>? readValue, Action<object?> writeValue)
    {
        var writeInterceptors = _subject.Context.GetServices<IWriteInterceptor>();
        var interception = new WritePropertyInterception(new PropertyReference(_subject, propertyName), readValue?.Invoke(), newValue); // TODO: reading here might be a problem (performance?)

        var returnWriteValue = new Func<WritePropertyInterception, object?>(value =>
        {
            writeValue(value.NewValue);
            return value.NewValue;
        });
    
        foreach (var handler in writeInterceptors)
        {
            var previousWriteValue = returnWriteValue;
            returnWriteValue = (context) =>
            {
                return handler.WriteProperty(context,
                    innerContext => previousWriteValue(innerContext));
            };
        }

        returnWriteValue(interception);
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

    public override void AddFallbackContext(IInterceptorSubjectContext context)
    {
        base.AddFallbackContext(context);
        
        foreach (var interceptor in context.GetServices<IInterceptor>())
        {
            interceptor.AttachTo(_subject);
        }
    }

    public override void RemoveFallbackContext(IInterceptorSubjectContext context)
    {
        foreach (var interceptor in context.GetServices<IInterceptor>())
        {
            interceptor.DetachFrom(_subject);
        }
        
        base.RemoveFallbackContext(context);
    }
}