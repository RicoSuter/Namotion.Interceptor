namespace Namotion.Interceptor;

public interface IInterceptorCollection
{
    IEnumerable<IInterceptor> Interceptors { get; }
    
    void AddInterceptor(IInterceptor interceptor);

    void RemoveInterceptor(IInterceptor interceptor);
    
    object? GetProperty(IInterceptorSubject subject, string propertyName, Func<object?> readValue);

    void SetProperty(IInterceptorSubject subject, string propertyName, object? newValue, Func<object?> readValue, Action<object?> writeValue);
}