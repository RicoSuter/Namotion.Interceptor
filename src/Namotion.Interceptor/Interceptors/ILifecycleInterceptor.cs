namespace Namotion.Interceptor.Interceptors;

/// <summary>
/// The single authority that owns structural graph membership for one context. Core provides the
/// attachment mechanism (the exact context on the executor and its compare-and-swap transitions);
/// this seam owns the policy: which subjects a structural edge pulls into the context, when an
/// anchor is consumed, and when an unreachable subject is released.
/// </summary>
/// <remarks>
/// A third-party implementation may store any graph representation and choose its own
/// synchronization model. It observes structural writes through <see cref="IWriteInterceptor"/> and
/// applies ownership through the raw executor transitions, so it needs no Tracking internals.
/// </remarks>
public interface ILifecycleInterceptor :
    IWriteInterceptor,
    ISingletonContextService<ILifecycleInterceptor>
{
    /// <summary>
    /// Attaches the subject to <paramref name="context"/> with the given root anchor, together with
    /// every subject its structural properties reach.
    /// </summary>
    /// <remarks>
    /// The whole prospective component is validated and claimed before any callback runs, so a
    /// rejected attach leaves no residue. A subject already owned by <paramref name="context"/> is
    /// promoted to <paramref name="anchor"/> without repeating its attach callbacks.
    /// </remarks>
    /// <param name="subject">The subject to attach.</param>
    /// <param name="context">The context to attach to; must be the context this interceptor owns.</param>
    /// <param name="anchor">The anchor to apply to <paramref name="subject"/>. Never
    /// <see cref="SubjectAnchorKind.None"/>: an attach without an anchor would be released again by
    /// the next reachability decision.</param>
    /// <exception cref="InvalidOperationException">The subject already carries an explicit anchor,
    /// or the subject or part of its component is owned by a different context.</exception>
    void AttachSubjectToContext(IInterceptorSubject subject, IInterceptorSubjectContext context, SubjectAnchorKind anchor);

    /// <summary>
    /// Clears the subject's root anchor on <paramref name="context"/> and releases the subject when
    /// no structural edge and no other anchor still holds it.
    /// </summary>
    /// <param name="subject">The subject to detach.</param>
    /// <param name="context">The context to detach from; must be the context this interceptor owns.</param>
    /// <exception cref="InvalidOperationException">The subject carries no explicit anchor on that
    /// context.</exception>
    void DetachSubjectFromContext(IInterceptorSubject subject, IInterceptorSubjectContext context);

    /// <summary>
    /// Transitional hook invoked when a context is composed onto a subject's executor as a fallback.
    /// It admits an unattached subject as a provisional root and seeds an already owned subject's
    /// structural properties, which is what drives the recursive attach descent while
    /// <see cref="IInterceptorSubjectContext.AddFallbackContext"/> still exists.
    /// </summary>
    void AttachSubjectToContext(IInterceptorSubject subject);

    /// <summary>
    /// Transitional counterpart of <see cref="AttachSubjectToContext(IInterceptorSubject)"/>: releases
    /// an owned subject regardless of its anchor, and does nothing for a subject this interceptor
    /// does not own.
    /// </summary>
    void DetachSubjectFromContext(IInterceptorSubject subject);
}
