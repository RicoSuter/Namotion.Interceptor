using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor;

public static class InterceptorSubjectExtensions
{
    /// <summary>
    /// Reads the subject's executor through <see cref="IInterceptorSubject.Executor"/>, which is
    /// the access path that survives the single-context cutover. Publishes an executor on first
    /// access; the attachment state lives on it, so an unattached subject needs one too.
    /// </summary>
    internal static IInterceptorExecutor GetExecutor(IInterceptorSubject subject)
    {
        return subject.Executor;
    }

    /// <summary>
    /// Gets the one exact context the subject is attached to, or null when it is unattached.
    /// </summary>
    public static IInterceptorSubjectContext? TryGetContext(this IInterceptorSubject subject)
    {
        return GetExecutor(subject).AttachedContext;
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
    /// Attaches the subject to <paramref name="context"/> with an explicit anchor, together with
    /// every subject its structural properties reach. Attaching a subject that is already attached
    /// to the same context promotes its anchor to <see cref="SubjectAnchorKind.Explicit"/> without
    /// repeating its attach callbacks. An explicit anchor is never set twice and a subject never
    /// moves directly between contexts; both throw before any state change.
    /// </summary>
    /// <exception cref="InvalidOperationException">The subject is already explicitly attached to
    /// this context, or the subject or part of its component is attached to a different context.</exception>
    public static void AttachToContext(this IInterceptorSubject subject, IInterceptorSubjectContext context)
    {
        var lifecycle = context.TryGetService<ILifecycleInterceptor>();
        if (lifecycle is not null)
        {
            lifecycle.AttachSubjectToContext(subject, context, SubjectAnchorKind.Explicit);
            return;
        }

        ApplyRootAnchor(subject, context, SubjectAnchorKind.Explicit);

        // Without a lifecycle the attach is root-only, but the context still has to become
        // resolvable from the subject.
        subject.Context.AddFallbackContext(context);
    }

    /// <summary>
    /// Clears the subject's explicit anchor on <paramref name="context"/>. The subject stays
    /// attached while a structural edge still holds it, and is released once nothing does.
    /// </summary>
    /// <exception cref="InvalidOperationException">The subject carries no explicit anchor, or its
    /// explicit anchor is on a different context.</exception>
    public static void DetachFromContext(this IInterceptorSubject subject, IInterceptorSubjectContext context)
    {
        var lifecycle = context.TryGetService<ILifecycleInterceptor>();
        if (lifecycle is not null)
        {
            lifecycle.DetachSubjectFromContext(subject, context);
            return;
        }

        var executor = GetExecutor(subject);
        while (true)
        {
            // One coherent snapshot; a transition interleaved between it and the update fails the
            // compare-and-swap below and retries against the fresh state.
            executor.TryGetAttachment(out var attachedContext, out var anchor, out var revision);
            ValidateExplicitDetach(attachedContext, anchor, context);

            if (executor.TryUpdateAttachment(revision, null, SubjectAnchorKind.None, out _))
            {
                break;
            }
        }

        subject.Context.RemoveFallbackContext(context);
    }

    /// <summary>
    /// Applies a root anchor to an unattached subject, or promotes the anchor of a subject already
    /// attached to the same context. Shared by the lifecycle-free attach path and by lifecycle
    /// implementations that need the same strict rules.
    /// </summary>
    internal static void ApplyRootAnchor(IInterceptorSubject subject, IInterceptorSubjectContext context, SubjectAnchorKind anchor)
    {
        var executor = GetExecutor(subject);
        while (true)
        {
            executor.TryGetAttachment(out var attachedContext, out var currentAnchor, out var revision);
            ValidateRootAnchor(attachedContext, currentAnchor, context, anchor);

            // A provisional anchor is a construction-time default and only ever applies to a fresh
            // subject: applying it to an attached one would demote an explicit root or turn an
            // inherited subject into a root that nothing ever releases.
            if (anchor == SubjectAnchorKind.Provisional && attachedContext is not null)
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
        SubjectAnchorKind currentAnchor,
        IInterceptorSubjectContext context,
        SubjectAnchorKind anchor)
    {
        if (attachedContext is not null && !ReferenceEquals(attachedContext, context))
        {
            throw new InvalidOperationException(
                "The subject is already attached to a different context. Detach it from that context first.");
        }

        if (anchor == SubjectAnchorKind.Explicit && currentAnchor == SubjectAnchorKind.Explicit)
        {
            throw new InvalidOperationException(
                "The subject is already explicitly attached to this context.");
        }
    }

    /// <summary>
    /// Rejects an explicit detach that has no explicit anchor to clear, or clears one on another
    /// context.
    /// </summary>
    internal static void ValidateExplicitDetach(
        IInterceptorSubjectContext? attachedContext,
        SubjectAnchorKind anchor,
        IInterceptorSubjectContext context)
    {
        if (anchor != SubjectAnchorKind.Explicit)
        {
            throw new InvalidOperationException("The subject has no explicit context anchor to detach.");
        }

        if (!ReferenceEquals(attachedContext, context))
        {
            throw new InvalidOperationException("The subject's explicit anchor is on a different context.");
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