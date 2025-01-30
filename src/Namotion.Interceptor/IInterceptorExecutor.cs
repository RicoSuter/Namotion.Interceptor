namespace Namotion.Interceptor;

public interface IInterceptorExecutor : IInterceptorSubjectContext
{
    object? GetProperty(IInterceptorSubject subject, string propertyName, Func<object?> readValue);

    void SetProperty(IInterceptorSubject subject, string propertyName, object? newValue, Func<object?> readValue, Action<object?> writeValue);
}