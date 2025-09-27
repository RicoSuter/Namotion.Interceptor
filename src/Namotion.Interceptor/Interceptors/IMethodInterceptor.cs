namespace Namotion.Interceptor.Interceptors;

public interface IMethodInterceptor
{
    object? InvokeMethod(MethodInvocationInterception context, Func<MethodInvocationInterception, object?> next);
}

public readonly struct MethodInvocationInterception
{
    public IInterceptorSubject Subject { get; }
    
    public string MethodName { get; }

    public object?[] Parameters { get; }
    
    public MethodInvocationInterception(IInterceptorSubject subject, string methodName, object?[] parameters)
    {
        Subject = subject;
        MethodName = methodName;
        Parameters = parameters;
    }
}