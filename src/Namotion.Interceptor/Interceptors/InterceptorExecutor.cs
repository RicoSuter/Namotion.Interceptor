using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.CompilerServices;
using Namotion.Interceptor.Cache;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Interceptors;

/// <summary>The built-in per-subject executor, terminal, revision owner, and attachment authority.</summary>
public sealed class InterceptorExecutor : IInterceptorExecutor
{
    [ThreadStatic]
    private static InterceptorSubjectContext? _logicalContext;

    [ThreadStatic]
    private static int _logicalContextDepth;

    private readonly IInterceptorSubject _subject;

    internal IInterceptorSubject Subject => _subject;

    internal readonly object SyncRoot = new();

    internal void CommitRawWriteLocked<TProperty>(
        ref PropertyWriteContext<TProperty> context,
        TProperty value,
        Action<IInterceptorSubject, TProperty> writeValue)
    {
        Debug.Assert(Monitor.IsEntered(SyncRoot));
        Debug.Assert(ReferenceEquals(context.Executor.Subject, context.Property.Subject));
        var captureRevision = BeginCaptureMutation();
        try
        {
            writeValue(_subject, value);
            context.IsWritten = true;
            context.IsTerminalCommitted = true;
            context.Revision = ++Revision;
            context.FinalizeOrigin();
            var isFromSource = context.Origin.Kind == ChangeOriginKind.FromSource;
            var timestamp = context.WriteTimestampRaw;
            context.Property.SetWriteState(timestamp > 0 ? timestamp : 0, context.Revision, isFromSource);
        }
        finally
        {
            CompleteCaptureMutation(captureRevision, context.StructuralLease is null ? Environment.CurrentManagedThreadId : 0);
        }
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

    internal long Revision;
    internal long CurrentRevision => Volatile.Read(ref Revision);
    private long _captureRevision;
    private int _captureWriterThreadId;
    private long _captureWriterRunStart;
    internal ManualResetEventSlim? CaptureMutationBlocked { get; set; }
    internal long CaptureRevision => Volatile.Read(ref _captureRevision);

    internal bool IsCaptureRevisionCurrent(long revision) =>
        (revision & 1) == 0 && CaptureRevision == revision;

    private long BeginCaptureMutation()
    {
        var spin = new SpinWait();
        long revision;
        while (((revision = CaptureRevision) & 1) != 0 ||
               Interlocked.CompareExchange(ref _captureRevision, revision + 1, revision) != revision)
        {
            CaptureMutationBlocked?.Set();
            spin.SpinOnce();
        }

        return revision;
    }

    private void CompleteCaptureMutation(long revision, int writerThreadId)
    {
        if (Volatile.Read(ref _captureWriterThreadId) != writerThreadId)
        {
            Volatile.Write(ref _captureWriterRunStart, revision + 2);
            Volatile.Write(ref _captureWriterThreadId, writerThreadId);
        }

        Volatile.Write(ref _captureRevision, revision + 2);
    }

    internal bool TryBeginMetadataPublication(long revision) =>
        (revision & 1) == 0 && Interlocked.CompareExchange(
            ref _captureRevision, revision + 1, revision) == revision;

    internal void CompleteMetadataPublication(long revision) =>
        CompleteCaptureMutation(revision, Environment.CurrentManagedThreadId);

    internal bool TryRefreshCapture(long revision, out long current)
    {
        current = CaptureRevision;
        return current == revision ||
               (current & 1) == 0 &&
               Volatile.Read(ref _captureWriterThreadId) == Environment.CurrentManagedThreadId &&
               unchecked(revision + 2 - Volatile.Read(ref _captureWriterRunStart)) >= 0 &&
               CaptureRevision == current;
    }

    private readonly object _attachmentLock = new();
    private volatile AttachmentState _attachment = AttachmentState.Unattached;
    private HashSet<StructuralWriteLease>? _activeStructuralLeases;
    private AttachmentTransition? _activeAttachmentTransition;
    private OwnershipReservation? _ownershipReservation;

    /// <summary>Creates the executor for one subject; generated subjects use <see cref="GetOrCreate"/>.</summary>
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
        InterceptorSubjectContext context, ReservationMode mode,
        ITopologyAdmissionCoordinator? coordinator = null,
        bool joinExclusive = false)
    {
        lock (_attachmentLock)
        {
            var current = _attachment;
            if (current.Phase != AttachmentPhase.Stable ||
                (current.Context is not null && !ReferenceEquals(current.Context, context)) ||
                (mode == ReservationMode.Exclusive || current.Context is null) &&
                current.StructuralLeaseCount != 0)
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
                     reservation.Mode == ReservationMode.Exclusive && !joinExclusive ||
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

    internal bool TryUpdateAttachmentAnchor(
        OwnershipReservationToken? reservation,
        long expectedRevision,
        InterceptorSubjectContext context,
        SubjectAttachmentAnchorKind anchor,
        out long currentRevision)
    {
        lock (_attachmentLock)
        {
            var current = _attachment;
            currentRevision = current.Revision;
            if (current.Revision != expectedRevision)
            {
                return false;
            }

            var reservationMatches = reservation is not null
                ? reservation.IsActive(this) && ReferenceEquals(_ownershipReservation, reservation.Reservation)
                : _ownershipReservation is null;
            if (current.Phase != AttachmentPhase.Stable ||
                !ReferenceEquals(current.Context, context) ||
                !reservationMatches)
            {
                throw LifecycleConflictException.Retryable(_subject);
            }

            currentRevision++;
            _attachment = new AttachmentState(
                context,
                anchor,
                currentRevision,
                AttachmentPhase.Stable,
                current.StructuralLeaseCount);
            return true;
        }
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

        if (context is not (null or InterceptorSubjectContext))
        {
            throw new InvalidOperationException(
                $"The context of type '{context.GetType().FullName}' is not a context created by " +
                "InterceptorSubjectContext.Create(). IInterceptorSubjectContext cannot be implemented " +
                "independently: interceptor chains compile inside the built-in implementation, so a " +
                "foreign context would attach without any interception.");
        }
    }

    internal StructuralWriteLease TryAcquireStructuralWriteLease() =>
        TryAcquireStructuralWriteLease(null, 0, validateContext: false, validateRevision: false, null);

    internal StructuralWriteLease TryAcquireStructuralWriteLease(
        InterceptorSubjectContext? expectedContext,
        ITopologyAdmissionCoordinator? coordinator = null) =>
        TryAcquireStructuralWriteLease(expectedContext, 0, validateContext: true, validateRevision: false, coordinator);

    private StructuralWriteLease TryAcquireStructuralWriteLease(
        InterceptorSubjectContext? expectedContext,
        long expectedRevision,
        bool validateContext,
        bool validateRevision,
        ITopologyAdmissionCoordinator? coordinator)
    {
        lock (_attachmentLock)
        {
            var current = _attachment;
            if ((validateContext && !ReferenceEquals(current.Context, expectedContext)) ||
                (validateRevision && current.Revision != expectedRevision))
            {
                throw new AttachmentRouteChangedException();
            }

            if (current.Phase != AttachmentPhase.Stable ||
                _ownershipReservation is { } reservation &&
                (reservation.Mode == ReservationMode.Exclusive || current.Context is null))
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

    internal bool HasStructuralWriteLease(InterceptorSubjectContext context)
    {
        lock (_attachmentLock)
        {
            return _activeStructuralLeases is { Count: > 0 } &&
                   ReferenceEquals(_attachment.Context, context);
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

    internal AttachmentTransition PrepareAttachmentUpdate(
        InterceptorSubjectContext? expectedContext,
        InterceptorSubjectContext? context,
        SubjectAttachmentAnchorKind anchor,
        OwnershipReservationToken? reservation = null)
    {
        ValidateAttachment(context, anchor);
        lock (_attachmentLock)
        {
            var current = _attachment;
            var isAnchorUpdate = context is not null && ReferenceEquals(current.Context, context);
            var reservationMatches = reservation is not null
                ? reservation.IsActive(this) && ReferenceEquals(_ownershipReservation, reservation.Reservation)
                : _ownershipReservation is null;
            if (!ReferenceEquals(current.Context, expectedContext) ||
                current.Phase != AttachmentPhase.Stable ||
                !isAnchorUpdate && current.StructuralLeaseCount != 0 ||
                !reservationMatches)
            {
                throw LifecycleConflictException.Retryable(_subject);
            }

            var preparedState = new AttachmentState(
                context,
                anchor,
                current.Revision + 1,
                AttachmentPhase.Stable,
                current.StructuralLeaseCount);
            var transition = new AttachmentTransition(this, preparedState);
            _activeAttachmentTransition = transition;
            _attachment = current.WithPhase(
                context is null ? AttachmentPhase.Detaching : AttachmentPhase.Attaching);
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

    private void PublishPreparedAttachmentTransition(AttachmentTransition transition)
    {
        lock (_attachmentLock)
        {
            _attachment = transition.PreparedState!;
            _activeAttachmentTransition = null;
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

    /// <summary>Returns or atomically publishes the subject's one executor.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static IInterceptorExecutor GetOrCreate(ref IInterceptorExecutor? context, IInterceptorSubject subject)
    {
        return context ?? CreateAndPublish(ref context, subject);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IInterceptorExecutor CreateAndPublish(ref IInterceptorExecutor? context, IInterceptorSubject subject)
    {
        var created = new InterceptorExecutor(subject);
        return Interlocked.CompareExchange(ref context, created, null) ?? created;
    }

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

    /// <summary>Reads a generated structural property through its synchronized raw reader.</summary>
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

    /// <summary>Writes a generated structural property through trusted raw delegates.</summary>
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
            var isMissingStructuralReader = false;
            if (coordinator is not null && readValue is null)
            {
                var metadata = new PropertyReference(_subject, propertyName).Metadata;
                isMissingStructuralReader = metadata.IsIntercepted && metadata.Type.CanContainSubjects() &&
                    metadata is not { IsDerived: true, IsDynamic: true, SetValue: null };
            }

            if (!IsAttachmentRoute(attachedContext, attachmentRevision))
            {
                continue;
            }

            if (isMissingStructuralReader)
            {
                throw new InvalidOperationException(
                    $"The attached structural property '{propertyName}' must provide a trusted raw reader and faithful raw writer.");
            }

            StructuralWriteLease lease;
            try
            {
                lease = coordinator is not null
                    ? coordinator.AcquireStructuralWriteLease(this)
                    : TryAcquireStructuralWriteLease(
                        attachedContext,
                        attachmentRevision,
                        validateContext: true,
                        validateRevision: true,
                        coordinator: null);
            }
            catch (AttachmentRouteChangedException)
            {
                continue;
            }

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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsAttachmentRoute(InterceptorSubjectContext? context, long revision)
    {
        var current = _attachment;
        return ReferenceEquals(current.Context, context) && current.Revision == revision;
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

        while (true)
        {
            var attachedContext = _attachment.Context;
            var lifecycle = attachedContext?.TryGetService<ILifecycleInterceptor>();
            if (lifecycle is null)
            {
                try { registration.PreparePublication(this); }
                catch (LifecycleConflictException) { continue; }

                lock (_attachmentLock)
                {
                    if (ReferenceEquals(_attachment.Context, attachedContext))
                    {
                        if (attachedContext is null && _ownershipReservation is not null)
                        {
                            throw LifecycleConflictException.Retryable(_subject);
                        }

                        if (registration.PublishPrepared(this)) return;
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

    internal sealed class AttachmentState
    {
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

        internal AttachmentTransition(InterceptorExecutor executor, AttachmentState preparedState)
        {
            _executor = executor;
            PreparedState = preparedState;
        }

        internal AttachmentState? PreparedState { get; }

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

        internal void PublishPrepared()
        {
            var executor = _executor!;
            executor.PublishPreparedAttachmentTransition(this);
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
