using System.Collections.Immutable;
using System.ComponentModel;
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
    /// Structural writes hold no attachment monitor while taking it.
    /// </summary>
    internal readonly object SyncRoot = new();

    /// <summary>
    /// Monotonic per-subject commit counter. Incremented by the terminal write while this executor's
    /// <see cref="SyncRoot"/> is held, so a plain increment is exclusive: no Interlocked needed. Dense
    /// over committed writes and never reset, so it stays comparable across detach and reattach.
    ///
    /// It records commit order, it does not establish it: the lock is what serializes the commits, and
    /// this counter labels them afterwards.
    ///
    /// Consumes a revision exactly when the terminal write runs. A vetoed write and a write stopped
    /// by the equality check never reach the terminal and consume nothing, but a derived property's
    /// recalculation does reach it (with a no-op write delegate) and takes a revision of its own.
    /// </summary>
    internal long Revision;

    // Context, anchor, revision, transition phase, and structural lease count are one immutable
    // publication. The monitor changes that publication and its private active-token identities;
    // structural interceptor chains run after releasing it, so their user code never holds it.
    private readonly object _attachmentLock = new();
    private volatile AttachmentState _attachment = AttachmentState.Unattached;
    private HashSet<long>? _activeStructuralLeaseIdentities;
    private long _nextStructuralLeaseIdentity;
    private long _activeAttachmentTransitionIdentity;
    private long _nextAttachmentTransitionIdentity;

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
    public IInterceptorSubjectContext? AttachedContext => _attachment.Context;

    /// <inheritdoc />
    public SubjectAttachmentAnchorKind AttachmentAnchor => _attachment.Anchor;

    /// <inheritdoc />
    public long AttachmentRevision => _attachment.Revision;

    internal int StructuralLeaseCount => _attachment.StructuralLeaseCount;

    internal AttachmentPhase CurrentAttachmentPhase => _attachment.Phase;

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

        var phase = context is null ? AttachmentPhase.Detaching : AttachmentPhase.Attaching;
        using var transition = TryAcquireAttachmentTransition(expectedRevision, phase, out currentRevision);
        if (transition is null)
        {
            return false;
        }

        transition.Commit((InterceptorSubjectContext?)context, anchor, out currentRevision);
        return true;
    }

    internal StructuralWriteLease TryAcquireStructuralWriteLease()
    {
        lock (_attachmentLock)
        {
            var current = _attachment;
            if (current.Phase != AttachmentPhase.Stable)
            {
                throw LifecycleConflictException.Retryable(_subject);
            }

            var identity = ++_nextStructuralLeaseIdentity;
            (_activeStructuralLeaseIdentities ??= []).Add(identity);
            _attachment = current.WithStructuralLeaseCount(current.StructuralLeaseCount + 1);
            return new StructuralWriteLease(this, identity, current.Context, current.Revision);
        }
    }

    internal void ReleaseStructuralWriteLease(long identity)
    {
        lock (_attachmentLock)
        {
            if (_activeStructuralLeaseIdentities?.Remove(identity) != true)
            {
                return;
            }

            var current = _attachment;
            _attachment = current.WithStructuralLeaseCount(current.StructuralLeaseCount - 1);
        }
    }

    internal AttachmentTransition? TryAcquireAttachmentTransition(
        long expectedRevision,
        AttachmentPhase phase,
        out long currentRevision)
    {
        if (phase == AttachmentPhase.Stable)
        {
            throw new ArgumentOutOfRangeException(nameof(phase));
        }

        lock (_attachmentLock)
        {
            var current = _attachment;
            currentRevision = current.Revision;
            if (current.Revision != expectedRevision)
            {
                return null;
            }

            if (current.Phase != AttachmentPhase.Stable || current.StructuralLeaseCount != 0)
            {
                throw LifecycleConflictException.Retryable(_subject);
            }

            var identity = ++_nextAttachmentTransitionIdentity;
            _activeAttachmentTransitionIdentity = identity;
            _attachment = current.WithPhase(phase);
            return new AttachmentTransition(this, identity);
        }
    }

    private void CommitAttachmentTransition(
        long identity,
        InterceptorSubjectContext? context,
        SubjectAttachmentAnchorKind anchor,
        out long currentRevision)
    {
        lock (_attachmentLock)
        {
            var current = _attachment;
            if (_activeAttachmentTransitionIdentity != identity || current.Phase == AttachmentPhase.Stable)
            {
                throw new InvalidOperationException("The attachment transition is no longer active.");
            }

            if (context is not null && current.Context is not null && !ReferenceEquals(current.Context, context))
            {
                throw new InvalidOperationException(
                    "Cannot attach the subject directly to a different context. Detach it to null first.");
            }

            currentRevision = current.Revision + 1;
            _attachment = new AttachmentState(
                context,
                anchor,
                currentRevision,
                AttachmentPhase.Stable,
                current.StructuralLeaseCount);
            _activeAttachmentTransitionIdentity = 0;
        }
    }

    private void ReleaseAttachmentTransition(long identity)
    {
        lock (_attachmentLock)
        {
            if (_activeAttachmentTransitionIdentity != identity)
            {
                return;
            }

            _activeAttachmentTransitionIdentity = 0;
            _attachment = _attachment.WithPhase(AttachmentPhase.Stable);
        }
    }

    /// <inheritdoc />
    public bool TryGetAttachment(out IInterceptorSubjectContext? context, out SubjectAttachmentAnchorKind anchor, out long revision)
    {
        var attachment = _attachment;
        context = attachment.Context;
        anchor = attachment.Anchor;
        revision = attachment.Revision;
        return context is not null;
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

    /// <summary>
    /// Returns whether generated accessors for <typeparamref name="TProperty"/> require structural
    /// synchronization. The JIT folds the per-type trait after its one-time initialization.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsStructuralProperty<TProperty>() =>
        InterceptorSubjectContext.PropertyTypeIndex<TProperty>.CanContainSubjects;

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
        var attachedContext = _attachment.Context;
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
    [EditorBrowsable(EditorBrowsableState.Never)]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TProperty GetGeneratedPropertyValue<TProperty>(
        string propertyName,
        Func<IInterceptorSubject, TProperty> readValue,
        bool executeInterceptors = true)
    {
        if (!executeInterceptors)
        {
            lock (SyncRoot)
            {
                return readValue(_subject);
            }
        }

        var attachedContext = _attachment.Context;
        if (attachedContext is null)
        {
            lock (SyncRoot)
            {
                return readValue(_subject);
            }
        }

        var context = new PropertyReadContext<TProperty>(
            this,
            new PropertyReference(_subject, propertyName),
            lockTerminal: true);
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

        var attachedContext = _attachment.Context;
        if (attachedContext is null)
        {
            UninterceptedChain<TProperty>.Write(ref context, writeValue);
        }
        else
        {
            attachedContext.ExecuteInterceptedWrite(propertyTypeIndex, ref context, writeValue);
        }

        return context.IsWritten;
    }

    /// <inheritdoc />
    [EditorBrowsable(EditorBrowsableState.Never)]
    public bool SetGeneratedPropertyValue<TProperty>(
        string propertyName,
        TProperty newValue,
        Func<IInterceptorSubject, TProperty> readValue,
        Action<IInterceptorSubject, TProperty> writeValue)
    {
        var propertyTypeIndex = InterceptorSubjectContext.PropertyTypeIndex<TProperty>.Value;
        return SetStructuralPropertyValue(
            propertyName,
            newValue,
            default!,
            readValue,
            writeValue,
            propertyTypeIndex);
    }

    /// <summary>
    /// The structural branch of the unified <c>SetPropertyValue</c> entry above. A shared lease pins
    /// its attachment while the chain runs; a racing exclusive transition fails promptly.
    /// </summary>
    private bool SetStructuralPropertyValue<TProperty>(string propertyName, TProperty newValue, TProperty currentValue, Action<IInterceptorSubject, TProperty> writeValue, int propertyTypeIndex) =>
        SetStructuralPropertyValue(propertyName, newValue, currentValue, null, writeValue, propertyTypeIndex);

    private bool SetStructuralPropertyValue<TProperty>(
        string propertyName,
        TProperty newValue,
        TProperty currentValue,
        Func<IInterceptorSubject, TProperty>? readValue,
        Action<IInterceptorSubject, TProperty> writeValue,
        int propertyTypeIndex)
    {
        using var lease = TryAcquireStructuralWriteLease();
        if (readValue is not null)
        {
            lock (SyncRoot)
            {
                currentValue = readValue(_subject);
            }
        }

        var attachedContext = lease.Context;
        var contextState = attachedContext?.PinState();
        var lifecycle = attachedContext?.TryGetServiceFromState<ILifecycleInterceptor>(contextState!);
        if (lifecycle is null)
        {
            return WriteStructuralValue(
                attachedContext,
                contextState,
                propertyName,
                newValue,
                currentValue,
                writeValue,
                propertyTypeIndex);
        }

        lifecycle.EnterStructuralWriteGate();
        try
        {
            return WriteStructuralValue(
                attachedContext,
                contextState,
                propertyName,
                newValue,
                currentValue,
                writeValue,
                propertyTypeIndex);
        }
        finally
        {
            lifecycle.ExitStructuralWriteGate();
        }
    }

    /// <summary>
    /// Runs a structural write against the context and chain state pinned by its lease. The caller
    /// may still hold the legacy lifecycle gate, but never the attachment monitor.
    /// </summary>
    private bool WriteStructuralValue<TProperty>(
        InterceptorSubjectContext? attachedContext,
        InterceptorSubjectContext.ContextState? contextState,
        string propertyName,
        TProperty newValue,
        TProperty currentValue,
        Action<IInterceptorSubject, TProperty> writeValue,
        int propertyTypeIndex)
    {
        if (attachedContext is null || contextState is null)
        {
            var writeContext = new PropertyWriteContext<TProperty>(
                this,
                new PropertyReference(_subject, propertyName),
                currentValue,
                newValue);
            UninterceptedChain<TProperty>.Write(ref writeContext, writeValue);
            return writeContext.IsWritten;
        }

        var context = new PropertyWriteContext<TProperty>(
            this,
            new PropertyReference(_subject, propertyName),
            currentValue,
            newValue);

        attachedContext.ExecuteInterceptedWrite(contextState, propertyTypeIndex, ref context, writeValue);
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

        // Deliberately never structural, unlike the public entry: the cascade targets derived
        // properties, whose values never establish edges, and the trigger may already hold the
        // attachment monitor, so entering the lifecycle gate here would invert the lock order.
        var attachedContext = _attachment.Context;
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
        // No state pin is needed here: that arm resolves no interceptor chain, so there is no
        // second state read that could disagree with the routing.
        while (true)
        {
            var attachedContext = _attachment.Context;
            var lifecycle = attachedContext?.TryGetService<ILifecycleInterceptor>();
            if (lifecycle is null)
            {
                lock (_attachmentLock)
                {
                    if (ReferenceEquals(_attachment.Context, attachedContext))
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
        var attachedContext = _attachment.Context;
        if (attachedContext is null)
        {
            // The zero-interceptor invoke chain is the direct invocation; see MethodInvocationFactory.
            return invokeMethod(_subject, parameters);
        }

        var context = new MethodInvocationContext(_subject, methodName, parameters);
        return attachedContext.ExecuteInterceptedInvoke(ref context, invokeMethod);
    }

    /// <summary>
    /// The attachment fields published together for coherent lock-free reads.
    /// </summary>
    private sealed class AttachmentState
    {
        /// <summary>The state every executor starts in, shared because it carries no identity.</summary>
        internal static readonly AttachmentState Unattached = new(
            null,
            SubjectAttachmentAnchorKind.None,
            0,
            AttachmentPhase.Stable,
            0);

        internal AttachmentState(
            InterceptorSubjectContext? context,
            SubjectAttachmentAnchorKind anchor,
            long revision,
            AttachmentPhase phase,
            int structuralLeaseCount)
        {
            Context = context;
            Anchor = anchor;
            Revision = revision;
            Phase = phase;
            StructuralLeaseCount = structuralLeaseCount;
        }

        internal AttachmentState WithPhase(AttachmentPhase phase) =>
            new(Context, Anchor, Revision, phase, StructuralLeaseCount);

        internal AttachmentState WithStructuralLeaseCount(int structuralLeaseCount) =>
            new(Context, Anchor, Revision, Phase, structuralLeaseCount);

        internal readonly InterceptorSubjectContext? Context;

        internal readonly SubjectAttachmentAnchorKind Anchor;

        internal readonly long Revision;

        internal readonly AttachmentPhase Phase;

        internal readonly int StructuralLeaseCount;
    }

    internal sealed class AttachmentTransition : IDisposable
    {
        private InterceptorExecutor? _executor;
        private readonly long _identity;

        internal AttachmentTransition(InterceptorExecutor executor, long identity)
        {
            _executor = executor;
            _identity = identity;
        }

        internal void Commit(
            InterceptorSubjectContext? context,
            SubjectAttachmentAnchorKind anchor,
            out long currentRevision)
        {
            var executor = _executor
                ?? throw new ObjectDisposedException(nameof(AttachmentTransition));
            executor.CommitAttachmentTransition(_identity, context, anchor, out currentRevision);
            Interlocked.CompareExchange(ref _executor, null, executor);
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _executor, null)?.ReleaseAttachmentTransition(_identity);
        }
    }
}
