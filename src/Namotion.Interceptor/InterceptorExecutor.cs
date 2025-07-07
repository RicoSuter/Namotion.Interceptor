namespace Namotion.Interceptor;

public class InterceptorExecutor : InterceptorSubjectContext, IInterceptorExecutor
{
    private readonly IInterceptorSubject _subject;

    public InterceptorExecutor(IInterceptorSubject subject)
    {
        _subject = subject;
    }
    
    public object? GetProperty(string propertyName, Func<object?> readValue)
    {
        return _subject.GetInterceptedProperty(propertyName, readValue);
    }
    
    public void SetProperty(string propertyName, object? newValue, Func<object?> readValue, Action<object?> writeValue)
    {
        _subject.SetInterceptedProperty(propertyName, newValue, readValue, writeValue);
    }

    public object? InvokeMethod(string methodName, object?[] parameters, Func<object?[], object?> invokeMethod)
    {
        return _subject.InvokeInterceptedMethod(methodName, parameters, invokeMethod);
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