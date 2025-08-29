namespace Namotion.Interceptor;

public interface IMethodInterceptor
{
    object? InvokeMethod(MethodInvocationInterception context, Func<MethodInvocationInterception, object?> next);
}