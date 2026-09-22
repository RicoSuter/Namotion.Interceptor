using System.Collections.Immutable;
using System.Runtime.CompilerServices;

namespace Namotion.Interceptor.Interceptors;

public sealed class InterceptorExecutor : InterceptorSubjectContext, IInterceptorExecutor
{
    private readonly IInterceptorSubject _subject;

    /// <summary>
    /// The subject this executor was constructed for. Exposed so the terminal write can assert that the
    /// context's executor and the locked subject are the same pairing its plain increment relies on.
    /// </summary>
    internal IInterceptorSubject Subject => _subject;

    /// <summary>
    /// Monotonic per-subject commit counter. Incremented by the terminal write while the subject's
    /// SyncRoot is held, so a plain increment is exclusive: no Interlocked needed. Dense over
    /// committed writes and never reset, so it stays comparable across detach and reattach.
    ///
    /// It records commit order, it does not establish it: the lock is what serializes the commits, and
    /// this counter labels them afterwards. Consumers do order changes by comparing it (see the flush
    /// merging in docs/tracking.md), but nothing in the write path depends on its value.
    ///
    /// Consumes a revision exactly when the terminal write runs. A vetoed write and a write stopped
    /// by the equality check never reach the terminal and consume nothing, but a derived property's
    /// recalculation does reach it (with a no-op write delegate) and takes a revision of its own.
    /// </summary>
    internal long Revision;

    public InterceptorExecutor(IInterceptorSubject subject)
    {
        _subject = subject;
    }

    /// <summary>
    /// Returns the subject's executor, publishing one on first access. Call it from the subject's
    /// <see cref="IInterceptorSubject.Context"/> accessor, passing that subject's own backing field.
    /// Public because the source generator emits the call into the consumer's assembly.
    /// </summary>
    /// <remarks>
    /// Compare-and-swap rather than <c>??=</c>: a lazy assignment lets two threads racing the first
    /// access each publish an executor and discard one, along with everything that had been put on it,
    /// including the per-subject commit revision counter. It is also the store that safely publishes
    /// the new instance, which a plain assignment is not.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static IInterceptorExecutor GetOrCreate(ref IInterceptorExecutor? context, IInterceptorSubject subject)
    {
        // The allocation sits in a separate non-inlined method so the accessor stays a load and a
        // branch, small enough to inline into its own callers.
        return context ?? CreateAndPublish(ref context, subject);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IInterceptorExecutor CreateAndPublish(ref IInterceptorExecutor? context, IInterceptorSubject subject)
    {
        var created = new InterceptorExecutor(subject);
        return Interlocked.CompareExchange(ref context, created, null) ?? created;
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TProperty GetPropertyValue<TProperty>(string propertyName, Func<IInterceptorSubject, TProperty> readValue)
    {
        var context = new PropertyReadContext<TProperty>(new PropertyReference(_subject, propertyName));
        return ExecuteInterceptedRead(ref context, readValue);
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool SetPropertyValue<TProperty>(string propertyName, TProperty newValue, TProperty currentValue, Action<IInterceptorSubject, TProperty> writeValue)
    {
        var context = new PropertyWriteContext<TProperty>(
            this,
            new PropertyReference(_subject, propertyName),
            currentValue,
            newValue);

        ExecuteInterceptedWrite(ref context, writeValue);
        return context.IsWritten;
    }

    /// <summary>
    /// Cascade re-entry path: skips the lazy-resolve machinery by pre-populating the new write
    /// context's timestamp cache. Lets the cascade share the trigger's captured time without
    /// pushing a <see cref="SubjectChangeContext.WithChangedTimestamp(DateTimeOffset?)"/> scope.
    /// The new value is already the stabilized getter output on this path, so publishing reuses it
    /// instead of invoking the getter again (see
    /// <see cref="PropertyWriteContext{TProperty}.FinalValueIsNewValue"/>).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool SetPropertyValue<TProperty>(string propertyName, TProperty newValue, TProperty currentValue, Action<IInterceptorSubject, TProperty> writeValue, long rawTimestamp)
    {
        var context = new PropertyWriteContext<TProperty>(
            this,
            new PropertyReference(_subject, propertyName),
            currentValue,
            newValue,
            rawTimestamp);

        ExecuteInterceptedWrite(ref context, writeValue);
        return context.IsWritten;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public object? InvokeMethod(string methodName, object?[] parameters, Func<IInterceptorSubject, object?[], object?> invokeMethod)
    {
        var context = new MethodInvocationContext(_subject, methodName, parameters);
        return ExecuteInterceptedInvoke(ref context, invokeMethod);
    }

    public override bool AddFallbackContext(IInterceptorSubjectContext context)
    {
        var result = base.AddFallbackContext(context);
        if (result)
        {
            var array = context.GetServices<ILifecycleInterceptor>();
            for (var index = 0; index < array.Length; index++)
            {
                var interceptor = array[index];
                interceptor.AttachSubjectToContext(_subject);
            }
        }

        return result;
    }

    /// <summary>
    /// Fallback contexts whose removal is under way on this executor. The detach callbacks run while
    /// the edge is still registered, because they resolve their handlers through this executor and
    /// would find nothing once it is gone, so the registration alone cannot tell a fresh removal
    /// from one already in flight. A removal of the same pair arriving in that window is refused:
    /// re-entered from a callback it would otherwise recurse until the stack is exhausted, and
    /// issued from another thread it would run the callbacks twice.
    /// </summary>
    private ImmutableArray<IInterceptorSubjectContext> _fallbackContextsBeingRemoved = ImmutableArray<IInterceptorSubjectContext>.Empty;

    public override bool RemoveFallbackContext(IInterceptorSubjectContext context)
    {
        if (!HasFallbackContext(context) || !TryClaimFallbackContextRemoval(context))
        {
            return false;
        }

        try
        {
            // A removal that completed between the check above and the claim has already run the
            // callbacks and dropped the edge.
            if (!HasFallbackContext(context))
            {
                return false;
            }

            var array = context.GetServices<ILifecycleInterceptor>();
            for (var index = 0; index < array.Length; index++)
            {
                var interceptor = array[index];
                interceptor.DetachSubjectFromContext(_subject);
            }

            return base.RemoveFallbackContext(context);
        }
        finally
        {
            ReleaseFallbackContextRemoval(context);
        }
    }

    private bool TryClaimFallbackContextRemoval(IInterceptorSubjectContext context)
    {
        return ImmutableInterlocked.Update(
            ref _fallbackContextsBeingRemoved,
            static (contexts, claimed) => contexts.Contains(claimed) ? contexts : contexts.Add(claimed),
            context);
    }

    private void ReleaseFallbackContextRemoval(IInterceptorSubjectContext context)
    {
        // A release always follows a claim of the same context, so a single entry is that context
        // and the shared empty array can stand in for the one Remove would allocate.
        ImmutableInterlocked.Update(
            ref _fallbackContextsBeingRemoved,
            static (contexts, released) => contexts.Length == 1
                ? ImmutableArray<IInterceptorSubjectContext>.Empty
                : contexts.Remove(released),
            context);
    }
}