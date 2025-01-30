namespace Namotion.Interceptor;

public interface IInterceptor
{
    void AttachTo(IInterceptorSubject subject);

    void DetachFrom(IInterceptorSubject subject);
}
