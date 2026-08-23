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

    // The exact attachment state. Writes are serialized by a private monitor rather than SyncRoot
    // so that transitioning the attachment never contends with ordinary property writes; reads are
    // lock-free (the context read is the ownership predicate across the codebase, so it must cost
    // a volatile load, not a monitor).
    //
    // Lock order, stated once here and cross-referenced everywhere else: the structural write
    // protocol is lifecycle gate, then _attachmentLock, then SyncRoot (the terminal). The gate
    // must come first: a write that entered this monitor before the gate deadlocks against a
    // removal that holds the gate and releases this subject's claim through this same monitor.
    // The attachment transitions (TryUpdateAttachment, TryGetAttachment) take _attachmentLock
    // alone and run no foreign code while holding it, so they are leaf acquisitions, and nothing
    // enters _attachmentLock while holding a SyncRoot.
    //
    // Publication ordering, which the lock-free readers depend on: a transition stores the context
    // and the anchor first (volatile, so release) and the revision last with an atomic release
    // store. A reader that pairs a revision with subsequently read fields can therefore see fields
    // that are NEWER than that revision, never older, and the compare-and-swap in
    // TryUpdateAttachment rejects exactly that case. The revision is 64-bit, so it is read with
    // Interlocked.Read: netstandard2.0 also targets 32-bit runtimes, where a plain long load can
    // tear.
    private readonly object _attachmentLock = new();
    private volatile IInterceptorSubjectContext? _attachedContext;
    private volatile SubjectAnchorKind _anchor;
    private long _attachmentRevision;

    public InterceptorExecutor(IInterceptorSubject subject)
    {
        _subject = subject;
    }

    public IInterceptorSubjectContext? AttachedContext => _attachedContext;

    public SubjectAnchorKind Anchor => _anchor;

    public long AttachmentRevision => Interlocked.Read(ref _attachmentRevision);

    public bool TryUpdateAttachment(long expectedRevision, IInterceptorSubjectContext? context, SubjectAnchorKind anchor, out long currentRevision)
    {
        if (context is null && anchor != SubjectAnchorKind.None)
        {
            throw new InvalidOperationException(
                $"Cannot apply the anchor '{anchor}' without an attached context.");
        }

        lock (_attachmentLock)
        {
            if (_attachmentRevision != expectedRevision)
            {
                currentRevision = _attachmentRevision;
                return false;
            }

            if (context is not null && _attachedContext is not null && !ReferenceEquals(_attachedContext, context))
            {
                throw new InvalidOperationException(
                    "Cannot attach the subject directly to a different context. Detach it to null first.");
            }

            // Fields first, revision last; see the publication ordering note on the fields above.
            _attachedContext = context;
            _anchor = anchor;
            currentRevision = _attachmentRevision + 1;
            Interlocked.Exchange(ref _attachmentRevision, currentRevision);
            return true;
        }
    }

    public bool TryGetAttachment(out IInterceptorSubjectContext? context, out SubjectAnchorKind anchor, out long revision)
    {
        lock (_attachmentLock)
        {
            context = _attachedContext;
            anchor = _anchor;
            revision = _attachmentRevision;
            return context is not null;
        }
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

    public bool SetStructuralPropertyValue<TProperty>(string propertyName, TProperty newValue, TProperty currentValue, Action<IInterceptorSubject, TProperty> writeValue)
    {
        // Lock order and why the gate comes first: see the note on _attachmentLock. Holding the
        // monitor from before chain resolution through the terminal is what turns a racing
        // attachment transition into ordering: the transition waits on the monitor instead of
        // invalidating an in-flight write.
        while (true)
        {
            var attachedContext = _attachedContext;
            var lifecycle = attachedContext?.TryGetService<ILifecycleInterceptor>();
            if (lifecycle is null)
            {
                // No lifecycle to order against: either the subject is unattached, or its context
                // has no lifecycle, so nothing downstream takes a topology gate for it. Two
                // assumptions, stated: interceptors resolved through a lifecycle-free context must
                // not take another context's lifecycle gate inside this chain, and a lifecycle
                // registered on the attached context after the resolution above is not seen by
                // this write, because the gate comes from that one resolution (contexts are
                // configured before subjects attach to them).
                lock (_attachmentLock)
                {
                    if (ReferenceEquals(_attachedContext, attachedContext))
                    {
                        return WriteStructuralValue(propertyName, newValue, currentValue, writeValue);
                    }
                }
            }
            else
            {
                lifecycle.EnterStructuralWriteGate();
                try
                {
                    lock (_attachmentLock)
                    {
                        // Revalidate under both locks: the attachment may have moved between the
                        // lock-free read above and the acquisitions. Falling out of the lock scopes
                        // releases both and retries against the fresh attachment.
                        if (ReferenceEquals(_attachedContext, attachedContext))
                        {
                            return WriteStructuralValue(propertyName, newValue, currentValue, writeValue);
                        }
                    }
                }
                finally
                {
                    lifecycle.ExitStructuralWriteGate();
                }
            }
        }
    }

    /// <summary>
    /// Commits a structural write while the caller holds the attachment monitor (and the lifecycle
    /// gate when the subject is attached to a context with a lifecycle).
    /// </summary>
    private bool WriteStructuralValue<TProperty>(string propertyName, TProperty newValue, TProperty currentValue, Action<IInterceptorSubject, TProperty> writeValue)
    {
        if (_attachedContext is null && HasEmptyState)
        {
            // Unattached and nothing composed: nothing intercepts this subject, so the write is a
            // plain backing store, as cheap as the pre-executor short circuit in the generated
            // helper. It still runs under the attachment monitor, so a concurrent attach either
            // sees the committed value when it seeds or waits until this write is done.
            writeValue(_subject, newValue);
            return true;
        }

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
                interceptor.OnContextComposed(_subject);
            }
        }

        return result;
    }

    public override bool RemoveFallbackContext(IInterceptorSubjectContext context)
    {
        if (HasFallbackContext(context))
        {
            var array = context.GetServices<ILifecycleInterceptor>();
            for (var index = 0; index < array.Length; index++)
            {
                var interceptor = array[index];
                interceptor.OnContextDecomposed(_subject);
            }

            return base.RemoveFallbackContext(context);
        }

        return false;
    }
}