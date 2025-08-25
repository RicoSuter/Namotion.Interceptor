namespace Namotion.Interceptor;

public interface IInterceptorExecutor : IInterceptorSubjectContext
{
    TProperty GetPropertyValue<TProperty>(string propertyName, Func<TProperty> readValue);

    void SetPropertyValue<TProperty>(string propertyName, TProperty newValue, Func<TProperty>? readValue, Action<TProperty> writeValue);
    
    object? InvokeMethod(string methodName, object?[] parameters, Func<object?[], object?> invokeMethod);
}