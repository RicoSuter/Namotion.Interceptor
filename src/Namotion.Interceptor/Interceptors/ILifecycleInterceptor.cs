namespace Namotion.Interceptor.Interceptors;

/// <summary>
/// Notified when a subject joins or leaves the lifecycle graph its context takes part in.
/// </summary>
/// <remarks>
/// On the root route, <see cref="SubjectAttachmentExtensions.AttachToContext"/> resolves the
/// interceptor set once and records it, and
/// <see cref="SubjectAttachmentExtensions.DetachFromContext"/> notifies exactly that recorded set
/// rather than resolving again. So an implementation registered after a root attached receives
/// neither that attach nor a later detach for it, and an implementation whose context has since
/// left the subject's chain still receives the detach it was owed. The property route resolves per
/// operation from the parent's context, so neither applies there.
/// </remarks>
public interface ILifecycleInterceptor
{
    /// <summary>
    /// Called when the specified subject begins to be intercepted by this interceptor.
    /// </summary>
    /// <param name="subject">The subject that will be intercepted.</param>
    void AttachSubjectToContext(IInterceptorSubject subject);

    /// <summary>
    /// Called when the specified subject is no longer intercepted by this interceptor.
    /// </summary>
    /// <param name="subject">The subject that is no longer being intercepted.</param>
    void DetachSubjectFromContext(IInterceptorSubject subject);
}
