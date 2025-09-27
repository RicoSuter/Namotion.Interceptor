namespace Namotion.Interceptor.Interceptors;

public interface IMethodInterceptor
{
    object? InvokeMethod(MethodInvocationContext context, InvokeMethodInterceptionDelegate next);
}

public delegate object? InvokeMethodInterceptionDelegate(ref MethodInvocationContext context);

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