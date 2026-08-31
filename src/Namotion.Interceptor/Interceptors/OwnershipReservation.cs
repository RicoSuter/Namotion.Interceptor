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
    private int _isCompleting;

    internal OwnershipReservationToken(
        InterceptorExecutor executor,
        OwnershipReservation reservation,
        ITopologyAdmissionCoordinator? coordinator = null)
    {
        _executor = executor;
        Reservation = reservation;
        Coordinator = coordinator;
    }

    internal OwnershipReservation Reservation { get; }

    internal ITopologyAdmissionCoordinator? Coordinator { get; }

    internal InterceptorExecutor Executor =>
        Volatile.Read(ref _isCompleting) == 0 && Volatile.Read(ref _executor) is { } executor
            ? executor
            : throw new ObjectDisposedException(nameof(OwnershipReservationToken));

    internal bool IsActive(InterceptorExecutor executor)
    {
        return Volatile.Read(ref _isCompleting) == 0 &&
            ReferenceEquals(Volatile.Read(ref _executor), executor);
    }

    internal bool IsActive(InterceptorSubjectContext context) =>
        Volatile.Read(ref _isCompleting) == 0 &&
        Volatile.Read(ref _executor) is { } executor &&
        executor.IsOwnershipReservationActive(this, context);

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
        if (Interlocked.CompareExchange(ref _isCompleting, 1, 0) != 0)
        {
            return;
        }

        try
        {
            var executor = Volatile.Read(ref _executor);
            if (executor is null)
                return;

            if (Coordinator is not null)
                Coordinator.CompleteOwnershipReservation(executor, this, retainCommittedOwnership);
            else
                executor.ReleaseOwnershipReservation(this, detachIfLast: !retainCommittedOwnership);
        }
        finally
        {
            Volatile.Write(ref _isCompleting, 0);
        }
    }

    internal void AcceptCompletion() => Interlocked.Exchange(ref _executor, null);
}
