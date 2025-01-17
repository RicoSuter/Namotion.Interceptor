namespace Namotion.Interceptor;

public interface IInterceptorProvider
{
    IEnumerable<IInterceptor> Interceptors { get; }
}