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
    /// Attaches the subject to <paramref name="context"/> with an explicit anchor. Attaching an
    /// unattached subject sets the context; attaching a subject already attached to the same
    /// context promotes its anchor to <see cref="SubjectAnchorKind.Explicit"/>. An explicit anchor
    /// is never set twice and a subject never moves directly between contexts; both throw before
    /// any state change.
    /// </summary>
    /// <exception cref="InvalidOperationException">The subject is already explicitly attached to
    /// this context, or is attached to a different context.</exception>
    public static void AttachToContext(this IInterceptorSubject subject, IInterceptorSubjectContext context)
    {
        var executor = GetExecutor(subject);
        while (true)
        {
            // One coherent snapshot; a transition interleaved between it and the update fails the
            // compare-and-swap below and retries against the fresh state.
            executor.TryGetAttachment(out var attachedContext, out var anchor, out var revision);

            if (attachedContext is not null)
            {
                if (!ReferenceEquals(attachedContext, context))
                {
                    throw new InvalidOperationException(
                        "The subject is already attached to a different context. Detach it from that context first.");
                }

                if (anchor == SubjectAnchorKind.Explicit)
                {
                    throw new InvalidOperationException(
                        "The subject is already explicitly attached to this context.");
                }
            }

            if (executor.TryUpdateAttachment(revision, context, SubjectAnchorKind.Explicit, out _))
            {
                break;
            }
        }

        // TODO(single-context-cutover): remove the fallback half once the exact attachment is
        // authoritative. Until then it keeps this extension driving the existing lifecycle
        // machinery exactly like the old AddFallbackContext idiom; a false return means the
        // fallback was already present (the promote case), which needs no lifecycle re-run.
        subject.Context.AddFallbackContext(context);
    }

    /// <summary>
    /// Clears the subject's explicit anchor on <paramref name="context"/>. Only the anchor is
    /// cleared in this stage; the attached context itself is released once structural attachment
    /// is authoritative.
    /// </summary>
    /// <exception cref="InvalidOperationException">The subject carries no explicit anchor, or its
    /// explicit anchor is on a different context.</exception>
    public static void DetachFromContext(this IInterceptorSubject subject, IInterceptorSubjectContext context)
    {
        var executor = GetExecutor(subject);
        while (true)
        {
            // One coherent snapshot; see AttachToContext.
            executor.TryGetAttachment(out var attachedContext, out var anchor, out var revision);

            if (anchor != SubjectAnchorKind.Explicit)
            {
                throw new InvalidOperationException("The subject has no explicit context anchor to detach.");
            }

            if (!ReferenceEquals(attachedContext, context))
            {
                throw new InvalidOperationException("The subject's explicit anchor is on a different context.");
            }

            if (executor.TryUpdateAttachment(revision, attachedContext, SubjectAnchorKind.None, out _))
            {
                break;
            }
        }

        // TODO(single-context-cutover): remove the fallback half once the exact attachment is
        // authoritative. Until then it drives the existing detach lifecycle exactly like the old
        // RemoveFallbackContext idiom.
        subject.Context.RemoveFallbackContext(context);
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