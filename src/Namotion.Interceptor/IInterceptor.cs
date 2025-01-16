namespace Namotion.Interceptor;

public interface IInterceptor
{
    public void AttachTo(IInterceptorSubject subject) { }
    
    public void DetachFrom(IInterceptorSubject subject) { }
}
