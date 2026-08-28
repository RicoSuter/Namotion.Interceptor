using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Cache;

internal static class WriteInterceptorFactory<TProperty>
{
    public static WriteAction<TProperty> Create(ImmutableArray<IWriteInterceptor> interceptors)
    {
        if (interceptors.Length == 0)
        {
            return static (ref context, innerWriteValue) =>
                ExecuteTerminal(ref context, innerWriteValue, chainHasLifecycle: false);
        }

        // Captured at chain compile time so the terminal's currency check costs a closure field
        // read per write instead of a per-write service resolution. The lifecycle is always last
        // in a compiled chain (the partition in InterceptorSubjectContext), so scanning here is
        // the same set the partition already classified.
        var chainHasLifecycle = false;
        foreach (var interceptor in interceptors)
        {
            if (interceptor is ILifecycleInterceptor)
            {
                chainHasLifecycle = true;
                break;
            }
        }

        var chain = new WriteInterceptorChain<TProperty>(
            interceptors,
            (ref context, innerWriteValue) =>
            {
                ExecuteTerminal(ref context, innerWriteValue, chainHasLifecycle);
                return context.NewValue;
            }
        );
        return chain.Execute;
    }

    /// <summary>
    /// The terminal write. A structural route runs the commit predicate under the subject's
    /// attachment monitor immediately around the commit, so a write commits only against the
    /// attachment its chain was resolved for; every other route commits directly. The predicate
    /// is gated on <see cref="PropertyWriteContext{TProperty}.IsStructuralRoute"/> alone; the
    /// terminal consults no metadata.
    /// </summary>
    private static void ExecuteTerminal(ref PropertyWriteContext<TProperty> context, Action<IInterceptorSubject, TProperty> innerWriteValue, bool chainHasLifecycle)
    {
        // Hoisted: innerWriteValue is opaque to the JIT, so a second read of the property
        // reference could not be folded into the first (PropertyReference advises this).
        var property = context.Property;
        var subject = property.Subject;

        if (!context.IsStructuralRoute)
        {
            Commit(ref context, innerWriteValue, property, subject);
            return;
        }

        var executor = context.Executor;
        lock (executor.AttachmentMonitor)
        {
            var actual = executor.AttachedContextExact;
            if (actual is not null)
            {
                if (!ReferenceEquals(actual, context.ExpectedAttachedContext))
                {
                    // The subject moved between routing and commit: the chain belongs to a world
                    // the subject has left, so the executor re-routes the whole write.
                    context.AttachmentMoved = true;
                    return;
                }

                // The currency check, scoped to lifecycle appearance: a chain compiled before a
                // WithLifecycle registration carries no gate section, so committing it here would
                // bypass the gate a concurrent post-registration write holds. Any other service
                // registered since the pin leaves the write alone (the next write sees it), so a
                // plain registration never disturbs an in-flight chain.
                if (!chainHasLifecycle && context.ChainState is { } chainState)
                {
                    var currentState = actual.PinState();
                    if (!ReferenceEquals(currentState, chainState) &&
                        actual.TryGetServiceFromState<ILifecycleInterceptor>(currentState) is not null)
                    {
                        context.AttachmentMoved = true;
                        return;
                    }
                }
            }

            // An unattached subject commits (the null rule): the monitor orders this commit
            // against every future claim, so a later attach's seeding reads the committed value.
            // A release by this thread's own chain is covered too, because the lifecycle's
            // write-through arm nulled ExpectedAttachedContext first, so a re-attach to the same
            // context before this point fails the reference check above and re-routes.
            Commit(ref context, innerWriteValue, property, subject);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Commit(ref PropertyWriteContext<TProperty> context, Action<IInterceptorSubject, TProperty> innerWriteValue, PropertyReference property, IInterceptorSubject subject)
    {
        lock (context.Executor.SyncRoot)
        {
            innerWriteValue(subject, context.NewValue);
            context.IsWritten = true;
            // Plain increment, no Interlocked: the enclosing lock and the revision counter live on
            // the same executor, so the increment is exclusive. The lock protects the SUBJECT's
            // backing fields, and the executor-belongs-to-subject half of that is only a
            // construction-site convention, so it is asserted. A mismatched executor would lock
            // another subject's terminal lock and increment another subject's counter, silently
            // and undetectably.
            Debug.Assert(ReferenceEquals(context.Executor.Subject, subject),
                "The context's executor must own the subject being locked: the terminal lock and the plain increment rely on that pairing.");
            context.Revision = ++context.Executor.Revision;
            // Before FinalizeOrigin, which demotes a stamped origin to Local when a hook changed
            // the value. That demotion is right for publishing and wrong here: the write still
            // came from the source, and counting it as local would let it discard a local write
            // that had already committed.
            var isFromSource = context.Origin.Kind == ChangeOriginKind.FromSource;

            context.FinalizeOrigin();
            var raw = context.WriteTimestampRaw;
            property.SetWriteState(raw > 0 ? raw : 0, context.Revision, isFromSource);
        }
    }
}
