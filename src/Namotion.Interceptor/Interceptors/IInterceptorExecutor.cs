namespace Namotion.Interceptor.Interceptors;

public interface IInterceptorExecutor : IInterceptorSubjectContext
{
    object? InvokeMethod(string methodName, object?[] parameters, Func<object?[], object?> invokeMethod);
}