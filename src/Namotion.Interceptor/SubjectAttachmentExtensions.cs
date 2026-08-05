using System.Collections.Immutable;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor;

/// <summary>
/// How a root subject joins and leaves a lifecycle graph. These live in core because the generated
/// constructor emits a call to <see cref="AttachToContext"/> without a Tracking reference, and
/// because a core-only consumer with its own <see cref="ILifecycleInterceptor"/> must be able to
/// undo what it attached: after this change <see cref="IInterceptorSubjectContext.RemoveFallbackContext"/>
/// rejects the attach edge, so there would otherwise be no core API to remove it.
/// </summary>
public static class SubjectAttachmentExtensions
{
    /// <summary>
    /// Attaches the subject and its whole subtree to the lifecycle graph the given context takes
    /// part in, and adds that context to the subject's resolution chain.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The subject is already attached through a different context, or already belongs to another
    /// lifecycle graph. A subject belongs to at most one graph.
    /// </exception>
    public static void AttachToContext(this IInterceptorSubject subject, IInterceptorSubjectContext context)
    {
        // Resolved before the edge is published and recorded with the attach. Resolving first means
        // a failing resolve leaves no edge behind. Recording it means the detach never re-resolves,
        // so a chain that has since turned cyclic cannot block the edge coming out, and attach and
        // detach pair exactly instead of each seeing whatever resolves at its own time.
        var interceptors = context.GetServices<ILifecycleInterceptor>();

        var executor = subject.GetExecutor();

        // No interceptor means there is no graph to join, so there is nothing to record: no
        // library-owned edge for RemoveFallbackContext to refuse, no interceptor to notify on
        // detach, and nothing that makes the subject attached. Recording it anyway would mark the
        // subject as belonging to a graph that does not exist, and that mark would then refuse
        // every later attempt to join a real one. So this call is exactly what it truthfully is,
        // plain composition. Its inverse is RemoveFallbackContext, not DetachFromContext: a later
        // DetachFromContext naming this same context finds no record, so it does nothing and leaves
        // the edge published here in place.
        if (interceptors.IsEmpty)
        {
            executor.AddFallbackContext(context);
            return;
        }

        if (!executor.TryRecordAttachContext(context, interceptors))
        {
            return;
        }

        try
        {
            executor.AddFallbackContext(context);

            for (var index = 0; index < interceptors.Length; index++)
            {
                interceptors[index].AttachSubjectToContext(subject);
            }
        }
        catch
        {
            // Rolls back this context's own state so a retry is possible. What it cannot roll back
            // is anything the lifecycle system already did before throwing: seeded reconciliation
            // entries and attached children stay. That residue is #384 and is out of scope.
            executor.ClearAttachContext(context);
            throw;
        }
    }

    /// <summary>
    /// Detaches the subject and its subtree from the lifecycle graph it was attached to through the
    /// given context, and removes that context from its resolution chain.
    /// </summary>
    /// <remarks>
    /// A subject attached through a context carrying no <see cref="ILifecycleInterceptor"/> has no
    /// attach record, so once it holds no parent references this method does nothing for it and
    /// leaves the composed edge in place.
    /// <see cref="IInterceptorSubjectContext.RemoveFallbackContext"/> is the inverse of that attach.
    /// While it is still referenced the reference-count guard runs first and throws, as it does for
    /// any other subject.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The subject is still referenced from parent properties, or was attached through a different
    /// context. Both are decided inside <see cref="InterceptorExecutor.TryClearAttachContext"/>,
    /// under the same lock that clears the record, so a rejection leaves the subject exactly as it
    /// was and cannot race the property attach that would invalidate it.
    /// </exception>
    public static void DetachFromContext(this IInterceptorSubject subject, IInterceptorSubjectContext context)
    {
        var executor = subject.GetExecutor();

        if (!executor.TryClearAttachContext(context, out var interceptors))
        {
            return;
        }

        try
        {
            for (var index = 0; index < interceptors.Length; index++)
            {
                interceptors[index].DetachSubjectFromContext(subject);
            }
        }
        finally
        {
            // Runs even when a detach interceptor throws, so the edge cannot outlive the detach.
            // The record is already clear, so the subject can be re-attached afterwards.
            executor.RemoveAttachEdge(context);
        }
    }

    /// <summary>
    /// Whether the subject takes part in a lifecycle graph, either as a root it was attached to or
    /// as a subject held by a parent property. A subject holding nothing but an explicit fallback
    /// context reports false.
    /// </summary>
    public static bool IsAttached(this IInterceptorSubject subject)
    {
        return subject.GetExecutor().IsAttachedCore;
    }

    /// <summary>
    /// The context the subject was root-attached through, which is the context
    /// <see cref="DetachFromContext"/> accepts. Null for a subject that was never root-attached,
    /// including an attached child: <see cref="IsAttached"/> answers whether the subject is in a
    /// graph, this answers which context would take it out.
    /// </summary>
    public static IInterceptorSubjectContext? TryGetAttachContext(this IInterceptorSubject subject)
    {
        return subject.GetExecutor().AttachContext;
    }

    internal static InterceptorExecutor GetExecutor(this IInterceptorSubject subject)
    {
        return subject.Context as InterceptorExecutor
            ?? throw new InvalidOperationException(
                $"Subject '{subject.GetType().FullName}' does not use an {nameof(InterceptorExecutor)} as its context, so " +
                "there is nowhere to record its position in the lifecycle graph. Subjects generated from " +
                "[InterceptorSubject] and DynamicSubject always do; a hand-written IInterceptorSubject must return an " +
                $"{nameof(InterceptorExecutor)} from its Context property.");
    }
}
