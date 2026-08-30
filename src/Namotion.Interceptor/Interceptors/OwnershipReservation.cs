namespace Namotion.Interceptor.Interceptors;

internal enum ReservationMode
{
    Shared,
    Exclusive
}

internal sealed class OwnershipReservation(
    InterceptorSubjectContext context,
    ReservationMode mode)
{
    internal InterceptorSubjectContext Context { get; } = context;
    internal ReservationMode Mode { get; } = mode;
    internal int ParticipantCount;
}

internal sealed class OwnershipReservationToken : IDisposable
{
    private InterceptorExecutor? _executor;

    internal OwnershipReservationToken(
        InterceptorExecutor executor,
        OwnershipReservation reservation,
        ITopologyAdmissionCoordinator? coordinator = null)
    {
        _executor = executor;
        Reservation = reservation;
        Subject = executor.Subject;
        Coordinator = coordinator;
    }

    internal OwnershipReservation Reservation { get; }

    internal IInterceptorSubject Subject { get; }

    internal ITopologyAdmissionCoordinator? Coordinator { get; }

    private InterceptorExecutor Executor => Volatile.Read(ref _executor)
        ?? throw new ObjectDisposedException(nameof(OwnershipReservationToken));

    internal bool IsActive(InterceptorExecutor executor)
    {
        return ReferenceEquals(Volatile.Read(ref _executor), executor);
    }

    internal bool TryUpdateAttachment(
        long expectedRevision,
        InterceptorSubjectContext context,
        SubjectAttachmentAnchorKind anchor,
        out long currentRevision)
    {
        return Executor.TryUpdateAttachment(this, expectedRevision, context, anchor, out currentRevision);
    }

    public void Dispose()
    {
        try
        {
            Complete(retainCommittedOwnership: true);
        }
        catch
        {
            // A dispose is the no-throw fallback for an abandoned participant.
        }
    }

    internal void Complete(bool retainCommittedOwnership)
    {
        var executor = Interlocked.Exchange(ref _executor, null);
        if (executor is null)
        {
            return;
        }

        if (Coordinator is not null)
        {
            Coordinator.CompleteOwnershipReservation(executor, this, retainCommittedOwnership);
        }
        else
        {
            executor.ReleaseOwnershipReservation(this, detachIfLast: !retainCommittedOwnership);
        }
    }
}
