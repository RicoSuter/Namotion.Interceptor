using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Namotion.Interceptor.Cache;

namespace Namotion.Interceptor.Interceptors;

/// <summary>
/// The built-in <see cref="IInterceptorExecutor"/>, one per subject and published on first access
/// through <see cref="GetOrCreate"/>. It additionally owns the per-subject state the interface
/// cannot express: the terminal lock, the commit revision, and the attachment monitor.
/// </summary>
public sealed class InterceptorExecutor : IInterceptorExecutor
{
    private readonly IInterceptorSubject _subject;

    /// <summary>
    /// The subject this executor was constructed for. Exposed so the terminal write can assert that the
    /// executor threaded through a write context and the subject being locked are the same pairing its
    /// plain increment relies on.
    /// </summary>
    internal IInterceptorSubject Subject => _subject;

    /// <summary>
    /// The terminal lock that serializes backing-field access of the subject, taken by the chain
    /// terminals in <see cref="ReadInterceptorFactory{TProperty}"/> and
    /// <see cref="WriteInterceptorFactory{TProperty}"/>. One executor is published per subject, so
    /// this is a per-subject lock; without it a wide value type could be read while half written.
    /// The innermost lock of the structural write order (see the note on <see cref="_attachmentLock"/>).
    /// </summary>
    internal readonly object SyncRoot = new();

    /// <summary>
    /// Monotonic per-subject commit counter. Incremented by the terminal write while this executor's
    /// <see cref="SyncRoot"/> is held, so a plain increment is exclusive: no Interlocked needed. Dense
    /// over committed writes and never reset, so it stays comparable across detach and reattach.
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
    // protocol acquires lifecycle gate, then _attachmentLock, then SyncRoot, and no library path
    // acquires in any other order. The write path holds no lock across the interceptor chain: the
    // lifecycle takes the gate inside the chain (it is the last interceptor in every compiled
    // write chain), and the terminal takes this monitor for the commit predicate and the commit
    // only, releasing it before any re-route retries. The gate still orders before this monitor
    // because claims and releases transition attachments through this monitor while holding the
    // gate. The attachment transitions (TryUpdateAttachment, TryGetAttachment) take
    // _attachmentLock alone and run no foreign code while holding it, so they are leaf
    // acquisitions, and nothing enters _attachmentLock while holding a SyncRoot.
    //
    // Publication ordering, which the lock-free readers depend on: a transition stores the context
    // and the anchor first (volatile, so release) and the revision last with an atomic release
    // store. A reader that pairs a revision with subsequently read fields can therefore see fields
    // that are NEWER than that revision, never older, and the compare-and-swap in
    // TryUpdateAttachment rejects exactly that case. The revision is 64-bit, so it is read with
    // Interlocked.Read: netstandard2.0 also targets 32-bit runtimes, where a plain long load can
    // tear.
    private readonly object _attachmentLock = new();
    private volatile InterceptorSubjectContext? _attachedContext;
    private volatile SubjectAttachmentAnchorKind _anchor;
    private long _attachmentRevision;

    /// <summary>
    /// The attachment monitor, exposed to the write terminal so the commit predicate and the
    /// commit run under it as one section; see the lock order note on <see cref="_attachmentLock"/>.
    /// </summary>
    internal object AttachmentMonitor => _attachmentLock;

    /// <summary>
    /// The exact attached context as the write terminal's predicate reads it: the volatile field,
    /// typed, without the interface indirection of <see cref="AttachedContext"/>.
    /// </summary>
    internal InterceptorSubjectContext? AttachedContextExact => _attachedContext;

    /// <summary>
    /// The bound on re-routed attempts of one logical write. A re-route needs a genuine
    /// attachment transition of the subject (or the once-per-context lifecycle registration), so
    /// exhausting the bound means user code is transitioning the subject on every attempt; the
    /// same bound the derived-property stabilization loop applies to user-code-driven
    /// instability.
    /// </summary>
    internal const int MaxWriteRouteAttempts = 100;

    /// <summary>
    /// Creates an executor for <paramref name="subject"/>. Prefer <see cref="GetOrCreate"/>, which
    /// publishes exactly one executor per subject; a second instance would split the commit
    /// revision and the terminal lock.
    /// </summary>
    /// <param name="subject">The subject this executor runs interception for.</param>
    public InterceptorExecutor(IInterceptorSubject subject)
    {
        _subject = subject;
    }

    /// <inheritdoc />
    public IInterceptorSubjectContext? AttachedContext => _attachedContext;

    /// <inheritdoc />
    public SubjectAttachmentAnchorKind AttachmentAnchor => _anchor;

    /// <inheritdoc />
    public long AttachmentRevision => Interlocked.Read(ref _attachmentRevision);

    /// <inheritdoc />
    public bool TryUpdateAttachment(long expectedRevision, IInterceptorSubjectContext? context, SubjectAttachmentAnchorKind anchor, out long currentRevision)
    {
        if (context is null && anchor != SubjectAttachmentAnchorKind.None)
        {
            throw new InvalidOperationException(
                $"Cannot apply the anchor '{anchor}' without an attached context.");
        }

        // Rejected loudly rather than attached uselessly: interceptor chains compile inside
        // InterceptorSubjectContext, so a foreign implementation of the interface would attach,
        // report itself through TryGetContext(), and intercept nothing.
        if (context is not (null or InterceptorSubjectContext))
        {
            throw new InvalidOperationException(
                $"The context of type '{context.GetType().FullName}' is not a context created by " +
                "InterceptorSubjectContext.Create(). IInterceptorSubjectContext cannot be implemented " +
                "independently: interceptor chains compile inside the built-in implementation, so a " +
                "foreign context would attach without any interception.");
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
            _attachedContext = (InterceptorSubjectContext?)context;
            _anchor = anchor;
            currentRevision = _attachmentRevision + 1;
            Interlocked.Exchange(ref _attachmentRevision, currentRevision);
            return true;
        }
    }

    /// <inheritdoc />
    public bool TryGetAttachment(out IInterceptorSubjectContext? context, out SubjectAttachmentAnchorKind anchor, out long revision)
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
    /// <see cref="IInterceptorSubject.Executor"/> accessor, passing that subject's own backing field.
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

    /// <summary>
    /// The chain an unattached subject's scalar write runs: nothing intercepts, so this is the
    /// zero-interceptor chain, the terminal write with its commit bookkeeping. Reads and method
    /// invocations need no counterpart because their zero-interceptor chains are the plain
    /// operations.
    /// </summary>
    private static class UninterceptedChain<TProperty>
    {
        internal static readonly WriteAction<TProperty> Write =
            WriteInterceptorFactory<TProperty>.Create(ImmutableArray<IWriteInterceptor>.Empty);
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TProperty GetPropertyValue<TProperty>(string propertyName, Func<IInterceptorSubject, TProperty> readValue)
    {
        var attachedContext = _attachedContext;
        if (attachedContext is null)
        {
            // The zero-interceptor read chain is the plain read, no terminal lock; see
            // ReadInterceptorFactory.
            return readValue(_subject);
        }

        var context = new PropertyReadContext<TProperty>(this, new PropertyReference(_subject, propertyName));
        return attachedContext.ExecuteInterceptedRead(ref context, readValue);
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool SetPropertyValue<TProperty>(string propertyName, TProperty newValue, TProperty currentValue, Action<IInterceptorSubject, TProperty> writeValue)
    {
        // The routing flag and the chain index are two fields of one per-type static class, read
        // together and threaded down, so a write pays for the generic statics access exactly once.
        var propertyTypeIndex = InterceptorSubjectContext.PropertyTypeIndex<TProperty>.Value;
        if (InterceptorSubjectContext.PropertyTypeIndex<TProperty>.CanContainSubjects)
        {
            return SetStructuralPropertyValue(propertyName, newValue, currentValue, writeValue, propertyTypeIndex);
        }

        var context = new PropertyWriteContext<TProperty>(
            this,
            new PropertyReference(_subject, propertyName),
            currentValue,
            newValue);

        var attachedContext = _attachedContext;
        if (attachedContext is null)
        {
            WriteUnattachedScalarValue(ref context, writeValue);
        }
        else
        {
            attachedContext.ExecuteInterceptedWrite(propertyTypeIndex, ref context, writeValue);
        }

        // A TProperty narrowed below a structural declared type can be re-routed by the
        // lifecycle's arms or by the unattached arm's commit predicate; a true-scalar write
        // never sets the flag, so this is one predicted branch on the hot path.
        return context.AttachmentMoved
            ? RetryScalarPropertyValue(propertyName, newValue, currentValue, writeValue, propertyTypeIndex, context.Attempted)
            : context.IsWritten;
    }

    /// <summary>
    /// The scalar branch's unattached arm. The declared-type consult is what closes the
    /// narrowed-unattached window: a write whose TProperty routes scalar but whose declared
    /// property type is structural must still answer the commit predicate, or an attach racing
    /// this write could seed past its commit and silently lose the edge. A true-scalar property
    /// sets nothing and keeps the plain terminal, since attach seeding never reads scalar
    /// properties.
    /// </summary>
    private void WriteUnattachedScalarValue<TProperty>(ref PropertyWriteContext<TProperty> context, Action<IInterceptorSubject, TProperty> writeValue)
    {
        if (_subject.Properties.TryGetValue(context.Property.Name, out var metadata) &&
            metadata.CanContainSubjects && !metadata.IsDerived)
        {
            context.IsStructuralRoute = true;
        }

        UninterceptedChain<TProperty>.Write(ref context, writeValue);
    }

    /// <summary>
    /// The scalar branch's bounded re-route loop, kept out of the inlined entry: it only runs for
    /// narrowed structural writes whose attachment moved between routing and commit.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool RetryScalarPropertyValue<TProperty>(string propertyName, TProperty newValue, TProperty currentValue, Action<IInterceptorSubject, TProperty> writeValue, int propertyTypeIndex, AttemptedOrigin attempted)
    {
        for (var attempt = 2; attempt <= MaxWriteRouteAttempts; attempt++)
        {
            var context = new PropertyWriteContext<TProperty>(
                this,
                new PropertyReference(_subject, propertyName),
                currentValue,
                newValue,
                in attempted);

            var attachedContext = _attachedContext;
            if (attachedContext is null)
            {
                WriteUnattachedScalarValue(ref context, writeValue);
            }
            else
            {
                attachedContext.ExecuteInterceptedWrite(propertyTypeIndex, ref context, writeValue);
            }

            if (!context.AttachmentMoved)
            {
                return context.IsWritten;
            }

            attempted = context.Attempted;
        }

        throw CreateRouteAttemptsExhaustedException(propertyName);
    }

    /// <summary>
    /// The structural branch of the unified <c>SetPropertyValue</c> entry above: coordinates with
    /// the lifecycle that owns the subject so an attach or detach racing this write orders against
    /// it rather than failing it. Kept out of the unified entry's body so the scalar route stays
    /// small enough to inline.
    /// </summary>
    /// <remarks>
    /// The executor holds no lock: topology atomicity lives with the lifecycle, which takes its
    /// gate inside the chain (the partition makes it the last interceptor), and commit-versus-
    /// transition atomicity lives with the terminal's commit predicate under the attachment
    /// monitor. The chain still resolves from one pinned context state, so it cannot contain a
    /// lifecycle the predicate's expectations did not see; a stale pin is answered at the
    /// terminal, not by a second routing decision.
    /// </remarks>
    private bool SetStructuralPropertyValue<TProperty>(string propertyName, TProperty newValue, TProperty currentValue, Action<IInterceptorSubject, TProperty> writeValue, int propertyTypeIndex)
    {
        var attempted = default(AttemptedOrigin);
        for (var attempt = 1; attempt <= MaxWriteRouteAttempts; attempt++)
        {
            // Each attempt constructs a fresh context, which is also the AttachmentMoved reset.
            // The first attempt consumes the thread-static pending origin; retries thread the
            // consumed origin through instead.
            var context = attempt == 1
                ? new PropertyWriteContext<TProperty>(this, new PropertyReference(_subject, propertyName), currentValue, newValue)
                : new PropertyWriteContext<TProperty>(this, new PropertyReference(_subject, propertyName), currentValue, newValue, in attempted);
            context.IsStructuralRoute = true;

            var attachedContext = _attachedContext;
            if (attachedContext is null)
            {
                // Unattached: the zero-interceptor chain, so the commit revision and the write
                // state publish exactly as on the scalar route. ExpectedAttachedContext and
                // ChainState stay null; the terminal's predicate orders the commit against a
                // racing claim through the monitor.
                UninterceptedChain<TProperty>.Write(ref context, writeValue);
            }
            else
            {
                context.ExpectedAttachedContext = attachedContext;
                var contextState = attachedContext.PinState();
                context.ChainState = contextState;
                attachedContext.ExecuteInterceptedWrite(contextState, propertyTypeIndex, ref context, writeValue);
            }

            if (!context.AttachmentMoved)
            {
                return context.IsWritten;
            }

            attempted = context.Attempted;
        }

        throw CreateRouteAttemptsExhaustedException(propertyName);
    }

    private Exception CreateRouteAttemptsExhaustedException(string propertyName)
    {
        return new InvalidOperationException(
            $"The write to property '{propertyName}' of the subject of type " +
            $"'{_subject.GetType().FullName}' was re-routed {MaxWriteRouteAttempts} times without " +
            "committing, because the subject's attachment kept transitioning between the routing " +
            "decision and the commit. An interceptor that detaches and re-attaches the subject on " +
            "every write attempt causes exactly this.");
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

        // Deliberately never structural, unlike the public entry: the cascade targets derived
        // properties, whose values never establish edges, and the trigger may already hold the
        // attachment monitor, so entering the lifecycle gate here would invert the lock order.
        var attachedContext = _attachedContext;
        if (attachedContext is null)
        {
            UninterceptedChain<TProperty>.Write(ref context, writeValue);
        }
        else
        {
            attachedContext.ExecuteInterceptedWrite(
                InterceptorSubjectContext.PropertyTypeIndex<TProperty>.Value, ref context, writeValue);
        }

        return context.IsWritten;
    }

    /// <inheritdoc />
    public void AddProperties(SubjectPropertyRegistration registration)
    {
        if (!ReferenceEquals(registration.Subject, _subject))
        {
            throw new InvalidOperationException(
                "The registration belongs to a different subject than this executor.");
        }

        // Same routing shape as SetStructuralPropertyValue: resolve the lifecycle from a lock-free
        // attachment read, let the lifecycle order the admission behind its own gate, and treat a
        // stale routing decision as a retry rather than an error. The unattached (or
        // lifecycle-free) arm publishes under the attachment monitor alone, so a concurrent attach
        // either sees the published metadata when it seeds or waits until the publication is done.
        // Unlike the structural write, no state pin is needed: the lifecycle-free arm publishes
        // metadata and resolves no interceptor chain, so there is no second state read that could
        // disagree with the routing.
        while (true)
        {
            var attachedContext = _attachedContext;
            var lifecycle = attachedContext?.TryGetService<ILifecycleInterceptor>();
            if (lifecycle is null)
            {
                lock (_attachmentLock)
                {
                    if (ReferenceEquals(_attachedContext, attachedContext))
                    {
                        registration.Publish();
                        return;
                    }
                }
            }
            else if (lifecycle.TryAddProperties(registration))
            {
                return;
            }
        }
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public object? InvokeMethod(string methodName, object?[] parameters, Func<IInterceptorSubject, object?[], object?> invokeMethod)
    {
        var attachedContext = _attachedContext;
        if (attachedContext is null)
        {
            // The zero-interceptor invoke chain is the direct invocation; see MethodInvocationFactory.
            return invokeMethod(_subject, parameters);
        }

        var context = new MethodInvocationContext(_subject, methodName, parameters);
        return attachedContext.ExecuteInterceptedInvoke(ref context, invokeMethod);
    }
}
