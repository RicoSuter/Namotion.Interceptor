namespace Namotion.Interceptor;

public interface IInterceptorCollection : IInterceptorProvider
{
    void AddInterceptors(params IEnumerable<IInterceptor> interceptors);

    void RemoveInterceptors(params IEnumerable<IInterceptor> interceptors);
    
    object? GetProperty(IInterceptorSubject subject, string propertyName, Func<object?> readValue);

    void SetProperty(IInterceptorSubject subject, string propertyName, object? newValue, Func<object?> readValue, Action<object?> writeValue);
}