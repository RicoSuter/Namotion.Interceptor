namespace Namotion.Interceptor.Interceptors;

public interface IMethodInterceptor
{
    object? InvokeMethod(MethodInvocationContext context, Func<MethodInvocationContext, object?> next);
}

public readonly struct MethodInvocationContext
{
    public IInterceptorSubject Subject { get; }
    
    public string MethodName { get; }

    public object?[] Parameters { get; }
    
    public MethodInvocationContext(IInterceptorSubject subject, string methodName, object?[] parameters)
    {
        Subject = subject;
        MethodName = methodName;
        Parameters = parameters;
    }
}