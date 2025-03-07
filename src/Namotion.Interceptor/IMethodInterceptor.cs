namespace Namotion.Interceptor;

public interface IMethodInterceptor : IInterceptor
{
    object? InvokeMethod(MethodInvocationInterception context, Func<MethodInvocationInterception, object?> next);
}