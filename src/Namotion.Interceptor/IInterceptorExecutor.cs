namespace Namotion.Interceptor;

public interface IInterceptorExecutor : IInterceptorSubjectContext
{
    object? GetProperty(string propertyName, Func<object?> readValue);

    void SetProperty(string propertyName, object? newValue, Func<object?> readValue, Action<object?> writeValue);
    
    object? InvokeMethod(string methodName, object?[] parameters, Func<object?[], object?> invokeMethod);
}