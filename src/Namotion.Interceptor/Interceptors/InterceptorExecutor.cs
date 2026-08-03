using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace Namotion.Interceptor.Interceptors;

public sealed class InterceptorExecutor : InterceptorSubjectContext, IInterceptorExecutor
{
    private readonly IInterceptorSubject _subject;

    public InterceptorExecutor(IInterceptorSubject subject)
    {
        _subject = subject;
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
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool SetPropertyValue<TProperty>(string propertyName, TProperty newValue, TProperty currentValue, Action<IInterceptorSubject, TProperty> writeValue, long rawTimestamp)
    {
        var context = new PropertyWriteContext<TProperty>(
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

    /// <remarks>
    /// The attach callbacks run after the edge is published, and they must: they resolve their
    /// handlers through this executor, which finds nothing until the fallback is in place.
    /// </remarks>
    public override bool AddFallbackContext(IInterceptorSubjectContext context)
    {
        // Cast first, matching the base mutator, so a foreign context fails here rather than
        // after an arbitrary service walk.
        var contextImpl = (InterceptorSubjectContext)context;
        if (HasFallbackContext(contextImpl))
        {
            return false;
        }

        // Reads the fallback's chain, not this one, so it does not need the edge. Resolving
        // before publishing is what leaves nothing behind when it throws.
        var interceptors = contextImpl.GetServices<ILifecycleInterceptor>();

        var attachment = TryBeginFallbackAttachment(contextImpl, interceptors);
        if (attachment is null)
        {
            return false;
        }

        var invokedInterceptorCount = 0;
        try
        {
            for (var index = 0; index < interceptors.Length; index++)
            {
                // Counted before the call: a thrower may have mutated itself, so its detach still
                // has to run.
                invokedInterceptorCount = index + 1;
                interceptors[index].AttachSubjectToContext(_subject);
            }
        }
        finally
        {
            if (CompleteFallbackAttachment(attachment, invokedInterceptorCount))
            {
                // A remover arrived mid-attach and handed its removal over. It has already told
                // its caller the edge is gone, so this must happen even while an attach exception
                // is propagating, and must not replace that exception.
                try
                {
                    DetachAndCompleteRemoval(attachment);
                }
                catch (Exception)
                {
                    // The attach failure is the one worth reporting.
                }
            }
        }

        return true;
    }

    /// <remarks>
    /// The detach callbacks run before the edge is removed, and they must: they resolve their
    /// handlers through this executor, which finds nothing once the fallback is gone.
    /// <para>
    /// Returning <c>true</c> means the removal is committed, not necessarily that the edge is
    /// already gone: when an add is still running its attach callbacks, the removal is handed to
    /// that thread and completes there. Waiting instead would deadlock, because the attaching
    /// thread is inside callbacks that take the lifecycle lock this caller may already hold.
    /// </para>
    /// </remarks>
    public override bool RemoveFallbackContext(IInterceptorSubjectContext context)
    {
        var contextImpl = (InterceptorSubjectContext)context;

        switch (TryTakeFallbackAttachment(contextImpl, out var attachment))
        {
            case FallbackRemovalOutcome.NotPresent:
                return false;

            case FallbackRemovalOutcome.Deferred:
                return true;

            default:
                DetachAndCompleteRemoval(attachment!);
                return true;
        }
    }

    /// <summary>
    /// Runs the recorded detach callbacks and then drops the edge. Best effort across the whole
    /// invoked prefix: the record is already claimed, so an interceptor skipped here could never
    /// be balanced by a later removal.
    /// </summary>
    private void DetachAndCompleteRemoval(FallbackAttachment attachment)
    {
        ExceptionDispatchInfo? failure = null;
        try
        {
            var interceptors = attachment.Interceptors;
            for (var index = 0; index < attachment.InvokedInterceptorCount; index++)
            {
                try
                {
                    interceptors[index].DetachSubjectFromContext(_subject);
                }
                catch (Exception exception)
                {
                    failure ??= ExceptionDispatchInfo.Capture(exception);
                }
            }
        }
        finally
        {
            // A handler failure must never block the removal, because a blocked removal is what
            // strands edges and retains subtrees.
            CompleteFallbackContextRemoval(attachment.Context);
        }

        failure?.Throw();
    }
}