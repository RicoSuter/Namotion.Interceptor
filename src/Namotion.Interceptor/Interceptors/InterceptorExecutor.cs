using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.CompilerServices;
using Namotion.Interceptor.Cache;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Interceptors;

/// <summary>
/// The built-in <see cref="IInterceptorExecutor"/>, one per subject and published on first access
/// through <see cref="GetOrCreate"/>. It additionally owns the per-subject state the interface
/// cannot express: the terminal lock, the commit revision, and the attachment monitor.
/// </summary>
public sealed class InterceptorExecutor : IInterceptorExecutor
{
    [ThreadStatic]
    private static InterceptorSubjectContext? _logicalContext;

    [ThreadStatic]
    private static int _logicalContextDepth;

    private readonly IInterceptorSubject _subject;

    // The subject paired with SyncRoot and the revision counter.
    internal IInterceptorSubject Subject => _subject;

    // Serializes backing-field access without holding the attachment monitor.
    internal readonly object SyncRoot = new();

    internal void CommitRawWriteLocked<TProperty>(
        ref PropertyWriteContext<TProperty> context,
        TProperty value,
        Action<IInterceptorSubject, TProperty> writeValue)
    {
        Debug.Assert(Monitor.IsEntered(SyncRoot));
        Debug.Assert(ReferenceEquals(context.Executor.Subject, context.Property.Subject));
        writeValue(_subject, value);
        context.IsWritten = true;
        context.IsTerminalCommitted = true;
        context.Revision = ++Revision;
        var isFromSource = context.Origin.Kind == ChangeOriginKind.FromSource;
        context.FinalizeOrigin();
        var timestamp = context.WriteTimestampRaw;
        context.Property.SetWriteState(timestamp > 0 ? timestamp : 0, context.Revision, isFromSource);
    }

    internal static LogicalContextScope EnterLogicalContext(InterceptorSubjectContext context)
    {
        if (_logicalContext is not null && !ReferenceEquals(_logicalContext, context))
        {
            throw new InvalidOperationException(
                "A thread runs topology work for at most one subject context at a time. Defer the second-context operation until the current operation completes.");
        }

        _logicalContext = context;
        _logicalContextDepth++;
        return new LogicalContextScope(true);
    }

    // Dense per-subject commit order, incremented only while SyncRoot is held.
    internal long Revision;

    // Attachment routing is one immutable publication guarded by this monitor for updates.
    private readonly object _attachmentLock = new();
    private volatile AttachmentState _attachment = AttachmentState.Unattached;
    private HashSet<StructuralWriteLease>? _activeStructuralLeases;
    private AttachmentTransition? _activeAttachmentTransition;
    private OwnershipReservation? _ownershipReservation;

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
        => TryUpdateAttachmentCore(null, expectedRevision, context, anchor, out currentRevision);

    internal OwnershipReservationToken TryAcquireOwnershipReservation(
        InterceptorSubjectContext context,
        ReservationMode mode,
        ITopologyAdmissionCoordinator? coordinator = null)
    {
        lock (_attachmentLock)
        {
            var current = _attachment;
            if (current.Phase != AttachmentPhase.Stable ||
                (current.Context is not null && !ReferenceEquals(current.Context, context)) ||
                (mode == ReservationMode.Exclusive && current.StructuralLeaseCount != 0))
            {
                throw LifecycleConflictException.Retryable(_subject);
            }

            var reservation = _ownershipReservation;
            if (reservation is null)
            {
                reservation = new OwnershipReservation(
                    context,
                    mode);
                _ownershipReservation = reservation;
            }
            else if (!ReferenceEquals(reservation.Context, context) ||
                     reservation.Mode == ReservationMode.Exclusive ||
                     mode == ReservationMode.Exclusive)
            {
                throw LifecycleConflictException.Retryable(_subject);
            }

            reservation.ParticipantCount++;
            return new OwnershipReservationToken(this, reservation, coordinator);
        }
    }

    internal bool IsOwnershipReservationActive(
        OwnershipReservationToken token,
        InterceptorSubjectContext context)
    {
        lock (_attachmentLock)
        {
            return token.IsActive(this) && ReferenceEquals(_ownershipReservation, token.Reservation) &&
                   ReferenceEquals(token.Reservation.Context, context);
        }
    }

    internal void ReleaseOwnershipReservation(OwnershipReservationToken token, bool detachIfLast)
    {
        lock (_attachmentLock)
        {
            var reservation = token.Reservation;
            if (!ReferenceEquals(_ownershipReservation, reservation))
            {
                return;
            }

            reservation.ParticipantCount--;
            if (reservation.ParticipantCount == 0)
            {
                var current = _attachment;
                if (detachIfLast && current.Phase == AttachmentPhase.Stable &&
                    current.StructuralLeaseCount == 0 &&
                    ReferenceEquals(current.Context, reservation.Context))
                {
                    _attachment = new AttachmentState(
                        null,
                        SubjectAttachmentAnchorKind.None,
                        current.Revision + 1,
                        AttachmentPhase.Stable,
                        0);
                }

                _ownershipReservation = null;
            }
        }
    }

    internal bool HasOwnershipReservation(InterceptorSubjectContext context)
    {
        lock (_attachmentLock)
        {
            return ReferenceEquals(_ownershipReservation?.Context, context);
        }
    }

    internal bool TryUpdateAttachment(
        OwnershipReservationToken reservation,
        long expectedRevision,
        InterceptorSubjectContext context,
        SubjectAttachmentAnchorKind anchor,
        out long currentRevision)
    {
        if (!ReferenceEquals(reservation.Reservation.Context, context))
        {
            throw new InvalidOperationException("The attachment context does not match the ownership reservation.");
        }

        return TryUpdateAttachmentCore(reservation, expectedRevision, context, anchor, out currentRevision);
    }

    private bool TryUpdateAttachmentCore(
        OwnershipReservationToken? reservation,
        long expectedRevision,
        IInterceptorSubjectContext? context,
        SubjectAttachmentAnchorKind anchor,
        out long currentRevision)
    {
        ValidateAttachment(context, anchor);
        var phase = context is null ? AttachmentPhase.Detaching : AttachmentPhase.Attaching;
        using var transition = TryAcquireAttachmentTransition(
            expectedRevision,
            phase,
            out currentRevision,
            reservation);
        if (transition is null)
        {
            return false;
        }

        transition.Commit((InterceptorSubjectContext?)context, anchor, out currentRevision);
        return true;
    }

    private static void ValidateAttachment(
        IInterceptorSubjectContext? context,
        SubjectAttachmentAnchorKind anchor)
    {
        if (context is null && anchor != SubjectAttachmentAnchorKind.None)
        {
            throw new InvalidOperationException(
                $"Cannot apply the anchor '{anchor}' without an attached context.");
        }

        // Interceptor chains compile inside InterceptorSubjectContext.
        if (context is not (null or InterceptorSubjectContext))
        {
            throw new InvalidOperationException(
                $"The context of type '{context.GetType().FullName}' is not a context created by " +
                "InterceptorSubjectContext.Create(). IInterceptorSubjectContext cannot be implemented " +
                "independently: interceptor chains compile inside the built-in implementation, so a " +
                "foreign context would attach without any interception.");
        }
    }

    internal StructuralWriteLease TryAcquireStructuralWriteLease(
        InterceptorSubjectContext? expectedContext = null,
        ITopologyAdmissionCoordinator? coordinator = null)
    {
        lock (_attachmentLock)
        {
            var current = _attachment;
            if (current.Phase != AttachmentPhase.Stable ||
                _ownershipReservation?.Mode == ReservationMode.Exclusive ||
                (expectedContext is not null && !ReferenceEquals(current.Context, expectedContext)))
            {
                throw LifecycleConflictException.Retryable(_subject);
            }

            var lease = new StructuralWriteLease(this, current.Context, current.Revision, coordinator);
            (_activeStructuralLeases ??= []).Add(lease);
            _attachment = current.WithStructuralLeaseCount(current.StructuralLeaseCount + 1);
            return lease;
        }
    }

    internal bool IsStructuralWriteLeaseActive(
        StructuralWriteLease lease,
        InterceptorSubjectContext context)
    {
        lock (_attachmentLock)
        {
            var current = _attachment;
            return _activeStructuralLeases?.Contains(lease) == true &&
                   current.Phase == AttachmentPhase.Stable &&
                   ReferenceEquals(current.Context, context);
        }
    }

    internal void ReleaseStructuralWriteLease(StructuralWriteLease lease)
    {
        lock (_attachmentLock)
        {
            if (_activeStructuralLeases?.Remove(lease) != true)
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
        out long currentRevision,
        OwnershipReservationToken? reservation = null)
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

            var reservationMatches = reservation is not null
                ? reservation.IsActive(this) &&
                  ReferenceEquals(_ownershipReservation, reservation.Reservation)
                : _ownershipReservation is null;
            if (current.Phase != AttachmentPhase.Stable || current.StructuralLeaseCount != 0 ||
                !reservationMatches)
            {
                throw LifecycleConflictException.Retryable(_subject);
            }

            var transition = new AttachmentTransition(this);
            _activeAttachmentTransition = transition;
            _attachment = current.WithPhase(phase);
            return transition;
        }
    }

    private void CommitAttachmentTransition(
        AttachmentTransition transition,
        InterceptorSubjectContext? context,
        SubjectAttachmentAnchorKind anchor,
        out long currentRevision)
    {
        lock (_attachmentLock)
        {
            var current = _attachment;
            if (!ReferenceEquals(_activeAttachmentTransition, transition) || current.Phase == AttachmentPhase.Stable)
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
            _activeAttachmentTransition = null;
        }
    }

    private void ReleaseAttachmentTransition(AttachmentTransition transition)
    {
        lock (_attachmentLock)
        {
            if (!ReferenceEquals(_activeAttachmentTransition, transition))
            {
                return;
            }

            _activeAttachmentTransition = null;
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

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IInterceptorExecutor CreateAndPublish(ref IInterceptorExecutor? context, IInterceptorSubject subject)
    {
        var created = new InterceptorExecutor(subject);
        return Interlocked.CompareExchange(ref context, created, null) ?? created;
    }

    // The zero-interceptor scalar chain still performs terminal commit bookkeeping.
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
            return readValue(_subject);
        }

        var context = new PropertyReadContext<TProperty>(this, new PropertyReference(_subject, propertyName));
        return attachedContext.ExecuteInterceptedRead(ref context, readValue);
    }

    /// <summary>
    /// Reads a generated structural property through its synchronized trusted raw reader.
    /// </summary>
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

        return context.IsTerminalCommitted;
    }

    /// <summary>
    /// Writes a generated structural property through trusted raw reader and writer delegates.
    /// </summary>
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

    // The structural branch pins attachment through the complete chain unwind.
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
        while (true)
        {
            var attachment = _attachment;
            var attachedContext = attachment.Context;
            var attachmentRevision = attachment.Revision;
            var contextState = attachedContext?.PinState();
            using var logicalScope = attachedContext is not null
                ? EnterLogicalContext(attachedContext)
                : default;
            var coordinator = attachedContext?.TryGetServiceFromState<ITopologyAdmissionCoordinator>(contextState!);
            if (coordinator is not null && readValue is null)
            {
                var metadata = new PropertyReference(_subject, propertyName).Metadata;
                if (metadata.IsIntercepted && metadata.Type.CanContainSubjects() &&
                    metadata is not { IsDerived: true, IsDynamic: true, SetValue: null })
                {
                    throw new InvalidOperationException(
                        $"The attached structural property '{propertyName}' must provide a trusted raw reader and faithful raw writer.");
                }
            }

            var lease = coordinator is not null
                ? coordinator.AcquireStructuralWriteLease(this)
                : TryAcquireStructuralWriteLease();
            if (!ReferenceEquals(lease.Context, attachedContext) ||
                lease.AttachmentRevision != attachmentRevision)
            {
                var retryException = lease.Complete(null);
                if (retryException is not null)
                {
                    ExceptionDispatchInfo.Capture(retryException).Throw();
                }

                continue;
            }

            Exception? primaryException = null;
            var committed = false;
            try
            {
                if (attachedContext is null && readValue is null)
                {
                    writeValue(_subject, newValue);
                    committed = true;
                }
                else
                {
                    committed = WriteStructuralValue(
                        attachedContext,
                        contextState,
                        propertyName,
                        newValue,
                        currentValue,
                        readValue,
                        writeValue,
                        propertyTypeIndex,
                        lease);
                }
            }
            catch (Exception exception)
            {
                primaryException = exception;
            }

            primaryException = lease.Complete(primaryException);
            if (primaryException is not null)
            {
                ExceptionDispatchInfo.Capture(primaryException).Throw();
            }

            return committed;
        }
    }

    private bool WriteStructuralValue<TProperty>(
        InterceptorSubjectContext? attachedContext,
        InterceptorSubjectContext.ContextState? contextState,
        string propertyName,
        TProperty newValue,
        TProperty currentValue,
        Func<IInterceptorSubject, TProperty>? readValue,
        Action<IInterceptorSubject, TProperty> writeValue,
        int propertyTypeIndex,
        StructuralWriteLease lease)
    {
        if (attachedContext is null || contextState is null)
        {
            var writeContext = new PropertyWriteContext<TProperty>(
                this,
                new PropertyReference(_subject, propertyName),
                currentValue,
                newValue);
            writeContext.ReadValue = readValue;
            UninterceptedChain<TProperty>.Write(ref writeContext, writeValue);
            return writeContext.IsTerminalCommitted;
        }

        var context = new PropertyWriteContext<TProperty>(
            this,
            new PropertyReference(_subject, propertyName),
            currentValue,
            newValue);
        context.ReadValue = readValue;
        context.StructuralLease = lease;

        attachedContext.ExecuteInterceptedWrite(contextState, propertyTypeIndex, ref context, writeValue);
        return context.IsTerminalCommitted;
    }

    // Cascade re-entry shares the trigger timestamp and never establishes structural edges.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool SetPropertyValue<TProperty>(string propertyName, TProperty newValue, TProperty currentValue, Action<IInterceptorSubject, TProperty> writeValue, long rawTimestamp)
    {
        var context = new PropertyWriteContext<TProperty>(
            this,
            new PropertyReference(_subject, propertyName),
            currentValue,
            newValue,
            rawTimestamp);

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

        return context.IsTerminalCommitted;
    }

    /// <inheritdoc />
    public void AddProperties(SubjectPropertyRegistration registration)
    {
        if (!ReferenceEquals(registration.Subject, _subject))
        {
            throw new InvalidOperationException(
                "The registration belongs to a different subject than this executor.");
        }

        // Admission revalidates the lock-free attachment route under its coordinator.
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
            return invokeMethod(_subject, parameters);
        }

        var context = new MethodInvocationContext(_subject, methodName, parameters);
        return attachedContext.ExecuteInterceptedInvoke(ref context, invokeMethod);
    }

    // Attachment fields published together for coherent lock-free reads.
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

        internal AttachmentTransition(InterceptorExecutor executor)
        {
            _executor = executor;
        }

        internal void Commit(
            InterceptorSubjectContext? context,
            SubjectAttachmentAnchorKind anchor,
            out long currentRevision)
        {
            var executor = _executor
                ?? throw new ObjectDisposedException(nameof(AttachmentTransition));
            executor.CommitAttachmentTransition(this, context, anchor, out currentRevision);
            Interlocked.CompareExchange(ref _executor, null, executor);
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _executor, null)?.ReleaseAttachmentTransition(this);
        }
    }

    internal readonly struct LogicalContextScope(bool isActive) : IDisposable
    {
        public void Dispose()
        {
            if (isActive && --_logicalContextDepth == 0)
            {
                _logicalContext = null;
            }
        }
    }
}
