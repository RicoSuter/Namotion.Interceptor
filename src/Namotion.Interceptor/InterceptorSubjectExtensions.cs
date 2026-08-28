using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor;

public static class InterceptorSubjectExtensions
{
    /// <summary>
    /// Gets the one exact context the subject is attached to, or null when it is unattached.
    /// </summary>
    /// <remarks>
    /// Reading <see cref="IInterceptorSubject.Executor"/> publishes an executor on first access;
    /// the attachment state lives on it, so an unattached subject needs one too.
    /// </remarks>
    public static IInterceptorSubjectContext? TryGetContext(this IInterceptorSubject subject)
    {
        return subject.Executor.AttachedContext;
    }

    /// <summary>
    /// Gets the one exact context the subject is attached to.
    /// </summary>
    /// <exception cref="InvalidOperationException">The subject is not attached to a context.</exception>
    public static IInterceptorSubjectContext GetContext(this IInterceptorSubject subject)
    {
        return TryGetContext(subject)
            ?? throw new InvalidOperationException("The subject is not attached to a context.");
    }

    /// <summary>
    /// Gets whether the subject is held in its context by a root anchor rather than only by an
    /// incoming structural edge.
    /// </summary>
    /// <remarks>
    /// Read this rather than inferring root-ness from an empty <c>GetParents()</c> result, which
    /// answers identically for a genuine root, for an unattached subject, and for a subject inside
    /// its own release. This separates the root from the other two, and pairs with
    /// <see cref="TryGetContext"/> when those two must also be told apart: a subject inside its own
    /// release still resolves its context, because the attachment is torn down after the detach
    /// callbacks rather than before them.
    ///
    /// A departing subject never reports true, by two different routes: an explicit detach clears
    /// the anchor before it releases, and the edge-removal path never releases an anchored subject
    /// at all.
    ///
    /// One lock-free read, deliberately: the anchor alone decides this, and the ownership
    /// predicates across the codebase must cost a volatile load rather than a monitor. A non-None
    /// anchor implies an attached context, which the executor enforces rather than assumes.
    /// </remarks>
    public static bool IsAnchoredRoot(this IInterceptorSubject subject)
    {
        return subject.Executor.AttachmentAnchor != SubjectAttachmentAnchorKind.None;
    }

    /// <summary>
    /// Attaches the subject to <paramref name="context"/> with an explicit anchor, together with
    /// every subject its structural properties reach. Attaching a subject that is already attached
    /// to the same context promotes its anchor to <see cref="SubjectAttachmentAnchorKind.Explicit"/> without
    /// repeating its attach callbacks. An explicit anchor is never set twice and a subject never
    /// moves directly between contexts; both throw before any state change.
    /// </summary>
    /// <exception cref="InvalidOperationException">The subject is already explicitly attached to
    /// this context, or the subject or part of its component is attached to a different context.</exception>
    public static void AttachToContext(this IInterceptorSubject subject, IInterceptorSubjectContext context)
    {
        AttachToContext(subject, context, SubjectAttachmentAnchorKind.Explicit);
    }

    /// <summary>
    /// Attaches the subject to <paramref name="context"/> with the given root anchor, together
    /// with every subject its structural properties reach. Context-taking constructors call this
    /// with <see cref="SubjectAttachmentAnchorKind.Provisional"/>, which anchors an unattached subject and
    /// leaves an already attached one alone; see <see cref="SubjectAttachmentAnchorKind"/> for how the two
    /// anchors differ. Public because generated and dynamic context-taking constructors emit the
    /// call from the consumer's assembly.
    /// </summary>
    /// <exception cref="InvalidOperationException">The anchor is
    /// <see cref="SubjectAttachmentAnchorKind.None"/>, the subject is already explicitly attached to this
    /// context, or the subject or part of its component is attached to a different context.</exception>
    public static void AttachToContext(this IInterceptorSubject subject, IInterceptorSubjectContext context, SubjectAttachmentAnchorKind anchor)
    {
        if (anchor == SubjectAttachmentAnchorKind.None)
        {
            throw new InvalidOperationException(
                "An attach without a root anchor would be released by the next reachability decision.");
        }

        var lifecycle = context.TryGetService<ILifecycleInterceptor>();
        if (lifecycle is not null)
        {
            lifecycle.AttachSubjectToContext(subject, context, anchor);
            return;
        }

        // Without a lifecycle the attach is root-only: publishing the exact context is what makes
        // the context's services resolvable from the subject.
        ApplyRootAnchor(subject, context, anchor);
    }

    /// <summary>
    /// Clears the subject's root anchor on <paramref name="context"/>. The subject stays attached
    /// while a structural edge still holds it, and is released once nothing does.
    /// </summary>
    /// <remarks>
    /// Either anchor kind can be cleared. A provisional anchor is detachable so that a caller who
    /// created a provisional root and then failed to give it a supporting edge can undo it; once an
    /// edge has consumed the anchor there is nothing to detach and the call is rejected, which is
    /// what <see cref="IsAnchoredRoot"/> answers ahead of the call.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The subject carries no anchor, or its anchor is
    /// on a different context.</exception>
    public static void DetachFromContext(this IInterceptorSubject subject, IInterceptorSubjectContext context)
    {
        var lifecycle = context.TryGetService<ILifecycleInterceptor>();
        if (lifecycle is not null)
        {
            lifecycle.DetachSubjectFromContext(subject, context);
            return;
        }

        var executor = subject.Executor;
        while (true)
        {
            // One coherent snapshot; a transition interleaved between it and the update fails the
            // compare-and-swap below and retries against the fresh state.
            executor.TryGetAttachment(out var attachedContext, out var anchor, out var revision);
            ValidateDetach(attachedContext, anchor, context);

            if (executor.TryUpdateAttachment(revision, null, SubjectAttachmentAnchorKind.None, out _))
            {
                break;
            }
        }
    }

    /// <summary>
    /// Applies a root anchor to an unattached subject, or promotes the anchor of a subject already
    /// attached to the same context. Shared by the lifecycle-free attach path and by lifecycle
    /// implementations that need the same strict rules.
    /// </summary>
    internal static void ApplyRootAnchor(IInterceptorSubject subject, IInterceptorSubjectContext context, SubjectAttachmentAnchorKind anchor)
    {
        var executor = subject.Executor;
        while (true)
        {
            executor.TryGetAttachment(out var attachedContext, out var currentAnchor, out var revision);
            ValidateRootAnchor(attachedContext, currentAnchor, context, anchor);

            // A provisional anchor is a construction-time default and only ever applies to a fresh
            // subject: applying it to an attached one would demote an explicit root or turn an
            // inherited subject into a root that nothing ever releases.
            if (anchor == SubjectAttachmentAnchorKind.Provisional && attachedContext is not null)
            {
                return;
            }

            if (executor.TryUpdateAttachment(revision, context, anchor, out _))
            {
                return;
            }
        }
    }

    /// <summary>
    /// Rejects a root anchor that would move a subject between contexts or set a second explicit
    /// anchor. A provisional anchor requested for an already anchored subject is left alone: the
    /// constructor route must not demote an explicit root.
    /// </summary>
    internal static void ValidateRootAnchor(
        IInterceptorSubjectContext? attachedContext,
        SubjectAttachmentAnchorKind currentAnchor,
        IInterceptorSubjectContext context,
        SubjectAttachmentAnchorKind anchor)
    {
        if (attachedContext is not null && !ReferenceEquals(attachedContext, context))
        {
            throw new InvalidOperationException(
                "The subject is already attached to a different context. Detach it from that context first.");
        }

        if (anchor == SubjectAttachmentAnchorKind.Explicit && currentAnchor == SubjectAttachmentAnchorKind.Explicit)
        {
            throw new InvalidOperationException(
                "The subject is already explicitly attached to this context.");
        }
    }

    /// <summary>
    /// Rejects an explicit detach that has no explicit anchor to clear, or clears one on another
    /// context.
    /// </summary>
    internal static void ValidateDetach(
        IInterceptorSubjectContext? attachedContext,
        SubjectAttachmentAnchorKind anchor,
        IInterceptorSubjectContext context)
    {
        if (anchor == SubjectAttachmentAnchorKind.None)
        {
            throw new InvalidOperationException("The subject has no context anchor to detach.");
        }

        if (!ReferenceEquals(attachedContext, context))
        {
            throw new InvalidOperationException("The subject's anchor is on a different context.");
        }
    }

    public static void SetData(this IInterceptorSubject subject, string key, object? value)
    {
        subject.Data[(null, key)] = value;
    }

    public static bool TryGetData(this IInterceptorSubject subject, string key, out object? value)
    {
        return subject.Data.TryGetValue((null, key), out value);
    }

    /// <summary>
    /// Adds subject data for the specified key only if the key is not already present.
    /// This operation is atomic and thread-safe, so it doubles as a one-shot latch: it returns
    /// <c>true</c> exactly once per subject and key.
    /// </summary>
    /// <returns><c>true</c> if the value was stored; <c>false</c> if a value was already present.</returns>
    public static bool TryAddData(this IInterceptorSubject subject, string key, object? value)
    {
        return subject.Data.TryAdd((null, key), value);
    }
}