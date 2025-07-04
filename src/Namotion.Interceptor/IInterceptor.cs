namespace Namotion.Interceptor;

public interface IInterceptor
{
    /// <summary>
    /// Called when the specified subject begins to be intercepted by this interceptor.
    /// </summary>
    /// <param name="subject">The subject that will be intercepted.</param>
    void AttachTo(IInterceptorSubject subject);

    /// <summary>
    /// Called when the specified subject is no longer intercepted by this interceptor.
    /// </summary>
    /// <param name="subject">The subject that is no longer being intercepted.</param>
    void DetachFrom(IInterceptorSubject subject);
}
