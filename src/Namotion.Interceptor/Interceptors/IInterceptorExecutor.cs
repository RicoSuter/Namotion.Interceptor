namespace Namotion.Interceptor.Interceptors;

public interface IInterceptorExecutor : IInterceptorSubjectContext
{
    TProperty GetPropertyValue<TProperty>(string propertyName, Func<IInterceptorSubject, TProperty> readValue);

    bool SetPropertyValue<TProperty>(string propertyName, TProperty newValue, Func<IInterceptorSubject, TProperty>? readValue, Action<IInterceptorSubject, TProperty> writeValue);
    
    object? InvokeMethod(string methodName, object?[] parameters, Func<IInterceptorSubject, object?[], object?> invokeMethod);
}