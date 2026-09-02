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
    private enum CaptureMutationKind
    {
        None,
        RawWrite,
        MetadataPublication,
        FinalDetachment
    }

    [ThreadStatic]
    private static InterceptorSubjectContext? _logicalContext;

    [ThreadStatic]
    private static int _logicalContextDepth;

    [ThreadStatic]
    private static int _logicalCallbackDepth;

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
        var captureRevision = BeginCaptureMutation(CaptureMutationKind.RawWrite);
        var commitGuard = context.CommitGuard;
        var guardEntered = false;
        try
        {
            if (commitGuard is not null)
            {
                if (!commitGuard.TryEnter())
                {
                    return;
                }

                guardEntered = true;
            }

            try
            {
                writeValue(_subject, value);
                context.IsWritten = true;
                context.IsTerminalCommitted = true;
                context.Revision = ++Revision;
                var isFromSource = context.Origin.Kind == ChangeOriginKind.FromSource;
                context.FinalizeOrigin();
                var timestamp = context.WriteTimestampRawForCommit;
                context.Property.SetWriteState(timestamp > 0 ? timestamp : 0, context.Revision, isFromSource);
            }
            finally
            {
                if (guardEntered)
                {
                    commitGuard!.Exit();
                }
            }
        }
        finally
        {
            CompleteCaptureMutation(
                captureRevision,
                context.StructuralLease is null ? Environment.CurrentManagedThreadId : 0);
        }
    }

    internal static LogicalContextScope EnterLogicalContext(InterceptorSubjectContext context)
    {
        if (_logicalContext is null)
        {
            _logicalContext = context;
        }
        else if (!ReferenceEquals(_logicalContext, context))
        {
            context.TryGetService<ILogicalContextGuard>()?.ThrowIfOtherLogicalContext();
            throw new InvalidOperationException(
                "A thread runs topology work for at most one subject context at a time.");
        }

        _logicalContextDepth++;
        return new LogicalContextScope(true);
    }

    internal static LogicalContextScope EnterLogicalCallback(InterceptorSubjectContext context)
    {
        EnterLogicalContext(context);
        _logicalCallbackDepth++;
        return new LogicalContextScope(true, true);
    }

    internal static bool IsInsideLogicalCallback => _logicalCallbackDepth > 0;

    internal static bool IsCurrentLogicalContext(InterceptorSubjectContext context) =>
        _logicalContext is null || ReferenceEquals(_logicalContext, context);

    internal long Revision;
    internal long CurrentRevision => Volatile.Read(ref Revision);
    private long _captureRevision;
    private int _captureMutationKind;
    private int _captureWriterThreadId;
    private long _captureWriterRunStart;
    internal ManualResetEventSlim? CaptureMutationBlocked { get; set; }
    internal long CaptureRevision => Volatile.Read(ref _captureRevision);

    internal bool IsCaptureRevisionCurrent(long revision) =>
        (revision & 1) == 0 && CaptureRevision == revision;

    private long BeginCaptureMutation(CaptureMutationKind kind)
    {
        var spin = new SpinWait();
        while (true)
        {
            lock (_attachmentLock)
            {
                if (_ownershipReservation is { Mode: ReservationMode.Exclusive })
                {
                    throw LifecycleConflictException.Retryable(_subject);
                }

                var revision = CaptureRevision;
                if ((revision & 1) == 0)
                {
                    Volatile.Write(ref _captureMutationKind, (int)kind);
                    if (Interlocked.CompareExchange(ref _captureRevision, revision + 1, revision) == revision)
                    {
                        return revision;
                    }

                    Volatile.Write(ref _captureMutationKind, (int)CaptureMutationKind.None);
                }
            }

            CaptureMutationBlocked?.Set();
            spin.SpinOnce();
        }
    }

    private void CompleteCaptureMutation(long revision, int writerThreadId)
    {
        if (Volatile.Read(ref _captureWriterThreadId) != writerThreadId)
        {
            Volatile.Write(ref _captureWriterRunStart, revision + 2);
            Volatile.Write(ref _captureWriterThreadId, writerThreadId);
        }

        Volatile.Write(ref _captureMutationKind, (int)CaptureMutationKind.None);
        Volatile.Write(ref _captureRevision, revision + 2);
    }

    private bool TryBeginMetadataPublication(long revision)
    {
        if ((revision & 1) != 0 || CaptureRevision != revision)
        {
            return false;
        }

        Volatile.Write(ref _captureMutationKind, (int)CaptureMutationKind.MetadataPublication);
        if (Interlocked.CompareExchange(ref _captureRevision, revision + 1, revision) == revision)
        {
            return true;
        }

        Volatile.Write(ref _captureMutationKind, (int)CaptureMutationKind.None);
        return false;
    }

    internal bool IsTransientCaptureConflict()
    {
        var kind = (CaptureMutationKind)Volatile.Read(ref _captureMutationKind);
        return kind is CaptureMutationKind.None or CaptureMutationKind.RawWrite ||
            (CaptureRevision & 1) == 0;
    }

    internal void CompleteMetadataPublication(long revision) =>
        CompleteCaptureMutation(revision, Environment.CurrentManagedThreadId);

    internal bool TryBeginMetadataPublication(
        long revision,
        AttachmentState attachment)
    {
        lock (_attachmentLock)
        {
            if (!ReferenceEquals(_attachment, attachment) || _activeAttachmentTransition is not null)
            {
                return false;
            }

            if (_ownershipReservation is not null)
            {
                throw LifecycleConflictException.Retryable(_subject);
            }

            return TryBeginMetadataPublication(revision);
        }
    }

    internal void CompleteReservedMetadataPublication(
        long revision,
        OwnershipReservationToken reservation)
    {
        lock (_attachmentLock)
        {
            Debug.Assert(reservation.IsActive(this));
            Debug.Assert(ReferenceEquals(_ownershipReservation, reservation.Reservation));
            Debug.Assert(CaptureRevision == revision);
            CompleteCaptureMutation(revision, Environment.CurrentManagedThreadId);
        }
    }

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
    private int _attachmentJournalThreadId;
    private int _pendingAttachmentFinalizations;
    private int _activeNonStructuralWrites;
    private int _usesGeneratedStructuralAccess;
    private List<IWriteCommitGuard>? _deferredWriteContinuations;

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

    internal int StructuralLeaseCount
    {
        get
        {
            lock (_attachmentLock)
            {
                return _activeStructuralLeases?.Count ?? 0;
            }
        }
    }

    internal AttachmentPhase CurrentAttachmentPhase => _attachment.Phase;

    internal bool SuppressGeneratedPrepublicationTimestamp =>
        _attachment.Revision == 0 && Volatile.Read(ref _usesGeneratedStructuralAccess) != 0;

    /// <inheritdoc />
    public bool TryUpdateAttachment(long expectedRevision, IInterceptorSubjectContext? context, SubjectAttachmentAnchorKind anchor, out long currentRevision)
    {
        ValidateAttachment(context, anchor);
        var phase = context is null ? AttachmentPhase.Detaching : AttachmentPhase.Attaching;
        using var transition = TryAcquireAttachmentTransition(expectedRevision, phase, out currentRevision);
        if (transition is null)
        {
            return false;
        }

        transition.Commit((InterceptorSubjectContext?)context, anchor, out currentRevision);
        return true;
    }

    internal OwnershipReservationToken TryAcquireOwnershipReservation(
        InterceptorSubjectContext context, ReservationMode mode,
        ITopologyAdmissionCoordinator? coordinator = null)
    {
        lock (_attachmentLock)
        {
            var current = _attachment;
            if ((CaptureRevision & 1) != 0)
            {
                throw IsTransientCaptureConflict()
                    ? LifecycleConflictException.TransientCapture(_subject)
                    : LifecycleConflictException.Retryable(_subject);
            }

            var isAttachingOwner = current.Phase == AttachmentPhase.Attaching &&
                _attachmentJournalThreadId == Environment.CurrentManagedThreadId;
            if (current.Phase != AttachmentPhase.Stable && !isAttachingOwner ||
                (current.Context is not null && !ReferenceEquals(current.Context, context)) ||
                (mode == ReservationMode.Exclusive || current.Context is null) &&
                (_activeStructuralLeases is { Count: > 0 } || _activeNonStructuralWrites != 0))
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

            var current = _attachment;
            var clearsAttachment = reservation.ParticipantCount == 1 &&
                detachIfLast && current.Phase == AttachmentPhase.Stable &&
                _activeStructuralLeases is not { Count: > 0 } &&
                ReferenceEquals(current.Context, reservation.Context);
            var detachedRevision = clearsAttachment
                ? GetNextAttachmentRevision(current.Revision)
                : current.Revision;
            reservation.ParticipantCount--;
            if (reservation.ParticipantCount == 0)
            {
                _ownershipReservation = null;
                if (clearsAttachment)
                {
                    _attachment = new AttachmentState(
                        null,
                        SubjectAttachmentAnchorKind.None,
                        detachedRevision,
                        AttachmentPhase.Stable);
                }

                reservation.Complete();
                QueueDeferredWriteContinuationsLocked();
            }

            token.AcceptCompletion();
        }
    }

    internal bool HasOwnershipReservation(InterceptorSubjectContext context)
    {
        lock (_attachmentLock)
        {
            return ReferenceEquals(_ownershipReservation?.Context, context);
        }
    }

    internal bool IsAttachedToOrHasReservation(
        InterceptorSubjectContext context,
        out OwnershipReservation? reservation)
    {
        lock (_attachmentLock)
        {
            var attachment = _attachment;
            if (ReferenceEquals(attachment.Context, context))
            {
                reservation = null;
                return true;
            }

            reservation = attachment.Context is null &&
                _ownershipReservation is { } candidate &&
                ReferenceEquals(candidate.Context, context)
                    ? candidate
                    : null;
            return false;
        }
    }

    internal AttachmentState AttachmentSnapshot => _attachment;

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

            var isAttachingOwner = current.Phase == AttachmentPhase.Attaching &&
                _attachmentJournalThreadId == Environment.CurrentManagedThreadId;
            if (current.Phase != AttachmentPhase.Stable && !isAttachingOwner ||
                _ownershipReservation is { } reservation &&
                (reservation.Mode == ReservationMode.Exclusive || current.Context is null))
            {
                throw LifecycleConflictException.Retryable(_subject);
            }

            var lease = new StructuralWriteLease(this, current.Context, current.Revision, coordinator);
            (_activeStructuralLeases ??= []).Add(lease);
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
                   (current.Phase == AttachmentPhase.Stable ||
                    current.Phase == AttachmentPhase.Attaching &&
                    _attachmentJournalThreadId == Environment.CurrentManagedThreadId) &&
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

            if (current.Phase != AttachmentPhase.Stable || _activeStructuralLeases is { Count: > 0 } ||
                _activeNonStructuralWrites != 0 ||
                (CaptureRevision & 1) != 0 ||
                _ownershipReservation is not null)
            {
                throw LifecycleConflictException.Retryable(_subject);
            }

            var transition = new AttachmentTransition(this, current);
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
            var removesAnchorUnderSharedReservation = reservation is null &&
                isAnchorUpdate && anchor == SubjectAttachmentAnchorKind.None &&
                _ownershipReservation is { Mode: ReservationMode.Shared } activeReservation &&
                ReferenceEquals(activeReservation.Context, current.Context);
            var reservationMatches = reservation is not null
                ? reservation.IsActive(this) && ReferenceEquals(_ownershipReservation, reservation.Reservation)
                : _ownershipReservation is null || removesAnchorUnderSharedReservation;
            var isAttachingOwner = current.Phase == AttachmentPhase.Attaching &&
                _attachmentJournalThreadId == Environment.CurrentManagedThreadId;
            if (!ReferenceEquals(current.Context, expectedContext) ||
                current.Phase != AttachmentPhase.Stable && !isAttachingOwner ||
                _activeNonStructuralWrites != 0 ||
                !isAnchorUpdate && _activeStructuralLeases is { Count: > 0 } ||
                !reservationMatches)
            {
                throw LifecycleConflictException.Retryable(_subject);
            }

            if ((CaptureRevision & 1) != 0)
            {
                throw IsTransientCaptureConflict()
                    ? LifecycleConflictException.TransientCapture(_subject)
                    : LifecycleConflictException.Retryable(_subject);
            }

            var isDetaching = context is null && current.Context is not null;
            var preparedRevision = GetNextAttachmentRevision(current.Revision);
            if (isDetaching)
            {
                _ = GetNextAttachmentRevision(preparedRevision);
            }

            var preparedState = isDetaching
                ? new AttachmentState(
                    current.Context,
                    SubjectAttachmentAnchorKind.None,
                    preparedRevision,
                    AttachmentPhase.Detaching)
                : new AttachmentState(
                    context,
                    anchor,
                    preparedRevision,
                    AttachmentPhase.Attaching);
            var transition = new AttachmentTransition(this, current, preparedState, isDetaching);
            _activeAttachmentTransition = transition;
            _attachment = current.WithPhase(
                isDetaching ? AttachmentPhase.Detaching : AttachmentPhase.Attaching);
            return transition;
        }
    }

    internal void FinalizeDetachment(
        InterceptorSubjectContext context,
        long expectedRevision)
    {
        var captureRevision = BeginFinalDetachmentCapture();
        try
        {
            FinalizeDetachmentUnderCapture(context, expectedRevision, captureRevision);
        }
        finally
        {
            CompleteFinalDetachmentCapture(captureRevision);
        }
    }

    internal long BeginFinalDetachmentCapture() => BeginCaptureMutation(CaptureMutationKind.FinalDetachment);

    internal void FinalizeDetachmentUnderCapture(
        InterceptorSubjectContext context,
        long expectedRevision,
        long captureRevision)
    {
        Debug.Assert(CaptureRevision == captureRevision + 1);
        lock (_attachmentLock)
        {
            var current = _attachment;
            if (current.Revision != expectedRevision ||
                current.Phase != AttachmentPhase.Detaching ||
                !ReferenceEquals(current.Context, context) ||
                current.Anchor != SubjectAttachmentAnchorKind.None ||
                _activeAttachmentTransition is not null)
            {
                return;
            }

            _attachment = new AttachmentState(
                null,
                SubjectAttachmentAnchorKind.None,
                GetNextAttachmentRevision(current.Revision),
                AttachmentPhase.Stable);
            _attachmentJournalThreadId = 0;
            _pendingAttachmentFinalizations = 0;
            QueueDeferredWriteContinuationsLocked();
        }
    }

    internal void CompleteFinalDetachmentCapture(long captureRevision) =>
        CompleteCaptureMutation(captureRevision, 0);

    internal void FinalizeAttachment(
        InterceptorSubjectContext context,
        long expectedRevision)
    {
        lock (_attachmentLock)
        {
            var current = _attachment;
            if (expectedRevision <= current.Revision &&
                current.Phase == AttachmentPhase.Attaching &&
                ReferenceEquals(current.Context, context) &&
                _activeAttachmentTransition is null &&
                _pendingAttachmentFinalizations > 0)
            {
                _pendingAttachmentFinalizations--;
                if (_pendingAttachmentFinalizations == 0)
                {
                    _attachment = current.WithPhase(AttachmentPhase.Stable);
                    _attachmentJournalThreadId = 0;
                    QueueDeferredWriteContinuationsLocked();
                }
            }
        }
    }

    internal void PreflightPotentialAttachmentUpdate(bool forceTransition = false)
    {
        lock (_attachmentLock)
        {
            if (forceTransition ||
                _attachment is { Context: null } or { Anchor: SubjectAttachmentAnchorKind.Provisional })
            {
                _ = GetNextAttachmentRevision(_attachment.Revision);
            }
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

            currentRevision = GetNextAttachmentRevision(current.Revision);
            _attachment = new AttachmentState(
                context,
                anchor,
                currentRevision,
                AttachmentPhase.Stable);
            _attachmentJournalThreadId = 0;
            _pendingAttachmentFinalizations = 0;
            _activeAttachmentTransition = null;
            QueueDeferredWriteContinuationsLocked();
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
            _attachment = transition.OriginalState;
            QueueDeferredWriteContinuationsLocked();
        }
    }

    private void PublishPreparedAttachmentTransition(AttachmentTransition transition)
    {
        lock (_attachmentLock)
        {
            _attachment = transition.PreparedState!;
            _attachmentJournalThreadId = Environment.CurrentManagedThreadId;
            if (!transition.IsPreparedDetachment)
            {
                _pendingAttachmentFinalizations++;
            }
            _activeAttachmentTransition = null;
        }
    }

    private static long GetNextAttachmentRevision(long revision)
    {
        if (revision == long.MaxValue)
        {
            throw new InvalidOperationException(
                "The attachment revision space is exhausted; publication cannot continue safely.");
        }

        return revision + 1;
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
        Volatile.Write(ref _usesGeneratedStructuralAccess, 1);
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

        return ExecuteNonStructuralWrite(
            propertyTypeIndex, ref context, writeValue);
    }

    /// <summary>Writes a generated structural property through trusted raw delegates.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public bool SetGeneratedPropertyValue<TProperty>(
        string propertyName,
        TProperty newValue,
        Func<IInterceptorSubject, TProperty> readValue,
        Action<IInterceptorSubject, TProperty> writeValue)
    {
        Volatile.Write(ref _usesGeneratedStructuralAccess, 1);
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
        if (readValue is not null)
        {
            lock (SyncRoot)
            {
                currentValue = readValue(_subject);
            }
        }

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
    internal bool SetDeferredPropertyValue<TProperty>(
        string propertyName,
        TProperty newValue,
        TProperty currentValue,
        Action<IInterceptorSubject, TProperty> writeValue,
        long rawTimestamp,
        IWriteCommitGuard commitGuard)
    {
        var context = new PropertyWriteContext<TProperty>(
            this,
            new PropertyReference(_subject, propertyName),
            currentValue,
            newValue,
            rawTimestamp);
        context.CommitGuard = commitGuard;

        if (!TryBeginDeferredNonStructuralWrite(commitGuard, out var attachedContext))
        {
            return false;
        }

        return ExecuteAdmittedNonStructuralWrite(
            attachedContext,
            InterceptorSubjectContext.PropertyTypeIndex<TProperty>.Value,
            ref context,
            writeValue);
    }

    private bool ExecuteNonStructuralWrite<TProperty>(
        int propertyTypeIndex,
        ref PropertyWriteContext<TProperty> context,
        Action<IInterceptorSubject, TProperty> writeValue)
    {
        var attachedContext = BeginNonStructuralWrite();
        return ExecuteAdmittedNonStructuralWrite(
            attachedContext, propertyTypeIndex, ref context, writeValue);
    }

    private bool ExecuteAdmittedNonStructuralWrite<TProperty>(
        InterceptorSubjectContext? attachedContext,
        int propertyTypeIndex,
        ref PropertyWriteContext<TProperty> context,
        Action<IInterceptorSubject, TProperty> writeValue)
    {
        Exception? primaryException = null;
        try
        {
            if (attachedContext is null)
            {
                UninterceptedChain<TProperty>.Write(ref context, writeValue);
            }
            else
            {
                attachedContext.ExecuteInterceptedWrite(propertyTypeIndex, ref context, writeValue);
            }
        }
        catch (Exception exception)
        {
            primaryException = exception;
        }
        finally
        {
            EndNonStructuralWrite();
        }

        if (attachedContext?.TryGetService<INonStructuralWriteCompletionCoordinator>() is { } coordinator)
        {
            primaryException = coordinator.CompleteNonStructuralWrite(primaryException);
        }

        if (primaryException is not null)
        {
            ExceptionDispatchInfo.Capture(primaryException).Throw();
        }

        return context.IsTerminalCommitted;
    }

    private bool TryBeginDeferredNonStructuralWrite(
        IWriteCommitGuard commitGuard,
        out InterceptorSubjectContext? attachedContext)
    {
        lock (_attachmentLock)
        {
            var current = _attachment;
            if ((current.Phase != AttachmentPhase.Stable &&
                 _attachmentJournalThreadId != Environment.CurrentManagedThreadId) ||
                _ownershipReservation is { Mode: ReservationMode.Exclusive })
            {
                if (commitGuard.TryDefer())
                {
                    (_deferredWriteContinuations ??= []).Add(commitGuard);
                }

                attachedContext = null;
                return false;
            }

            _activeNonStructuralWrites++;
            attachedContext = current.Context;
            return true;
        }
    }

    private InterceptorSubjectContext? BeginNonStructuralWrite()
    {
        lock (_attachmentLock)
        {
            var current = _attachment;
            if (current.Phase != AttachmentPhase.Stable &&
                _attachmentJournalThreadId != Environment.CurrentManagedThreadId ||
                _ownershipReservation is { Mode: ReservationMode.Exclusive })
            {
                throw LifecycleConflictException.Retryable(_subject);
            }

            _activeNonStructuralWrites++;
            return current.Context;
        }
    }

    private void EndNonStructuralWrite()
    {
        lock (_attachmentLock)
        {
            Debug.Assert(_activeNonStructuralWrites > 0);
            _activeNonStructuralWrites--;
        }
    }

    private void QueueDeferredWriteContinuationsLocked()
    {
        Debug.Assert(Monitor.IsEntered(_attachmentLock));
        if (_attachment.Phase != AttachmentPhase.Stable ||
            _ownershipReservation is { Mode: ReservationMode.Exclusive })
        {
            return;
        }

        var continuations = _deferredWriteContinuations;
        _deferredWriteContinuations = null;
        if (continuations is null)
        {
            return;
        }

        ThreadPool.UnsafeQueueUserWorkItem(
            static state =>
            {
                foreach (var deferredWrite in (List<IWriteCommitGuard>)state!)
                {
                    try
                    {
                        deferredWrite.Resume();
                    }
                    catch (Exception exception)
                    {
                        try
                        {
                            Trace.TraceError($"Completing a deferred property write failed: {exception}");
                        }
                        catch
                        {
                            // Deferred completion remains no-throw when diagnostics are misconfigured.
                        }
                    }
                }
            },
            continuations);
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
            var attachment = _attachment;
            var attachedContext = attachment.Context;
            var lifecycle = attachedContext?.TryGetService<ILifecycleInterceptor>();
            if (lifecycle is null)
            {
                try { registration.PreparePublication(this); }
                catch (LifecycleConflictException conflict) when (conflict.IsTransientCapture) { continue; }

                if (!registration.TryPublishPrepared(this, attachment))
                {
                    continue;
                }
                return;
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
            AttachmentPhase.Stable);

        internal AttachmentState(
            InterceptorSubjectContext? context,
            SubjectAttachmentAnchorKind anchor,
            long revision,
            AttachmentPhase phase)
        {
            Context = context;
            Anchor = anchor;
            Revision = revision;
            Phase = phase;
        }

        internal AttachmentState WithPhase(AttachmentPhase phase) =>
            new(Context, Anchor, Revision, phase);

        internal readonly InterceptorSubjectContext? Context;

        internal readonly SubjectAttachmentAnchorKind Anchor;

        internal readonly long Revision;

        internal readonly AttachmentPhase Phase;
    }

    internal sealed class AttachmentTransition : IDisposable
    {
        private InterceptorExecutor? _executor;

        internal AttachmentTransition(InterceptorExecutor executor, AttachmentState originalState)
        {
            _executor = executor;
            OriginalState = originalState;
        }

        internal AttachmentTransition(
            InterceptorExecutor executor,
            AttachmentState originalState,
            AttachmentState preparedState,
            bool isPreparedDetachment)
        {
            _executor = executor;
            OriginalState = originalState;
            PreparedState = preparedState;
            IsPreparedDetachment = isPreparedDetachment;
        }

        internal AttachmentState OriginalState { get; }

        internal AttachmentState? PreparedState { get; }

        internal bool IsPreparedDetachment { get; }

        internal InterceptorExecutor Executor => _executor
            ?? throw new ObjectDisposedException(nameof(AttachmentTransition));

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

    internal readonly struct LogicalContextScope(bool isActive, bool isCallback = false) : IDisposable
    {
        public void Dispose()
        {
            if (isCallback)
            {
                _logicalCallbackDepth--;
            }

            if (isActive && --_logicalContextDepth == 0)
            {
                _logicalContext = null;
            }
        }
    }
}
