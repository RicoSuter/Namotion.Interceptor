using Namotion.Interceptor;

namespace HomeBlaze.Services;

public static class ProvisionalAnchorExtensions
{
    /// <summary>
    /// Detaches a subject whose context-taking constructor anchored it provisionally to
    /// <paramref name="context"/> and that no property has taken over since. Does nothing for a subject
    /// that a property holds, because the supporting edge consumed the anchor, nor for a subject that
    /// was created detached or attached explicitly.
    /// </summary>
    public static void DetachIfProvisionallyAnchored(this IInterceptorSubject subject, IInterceptorSubjectContext context)
    {
        subject.Executor.TryGetAttachment(out var attachedContext, out var anchor, out _);
        if (ReferenceEquals(attachedContext, context) && anchor == SubjectAttachmentAnchorKind.Provisional)
        {
            subject.DetachFromContext(context);
        }
    }
}
