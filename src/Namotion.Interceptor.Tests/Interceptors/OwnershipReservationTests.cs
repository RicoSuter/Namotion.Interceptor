using System.Reflection;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Testing;

namespace Namotion.Interceptor.Tests.Interceptors;

public class OwnershipReservationTests
{
    private sealed class BlockingReservationCoordinator : ITopologyAdmissionCoordinator
    {
        internal ManualResetEventSlim CompletionEntered { get; } = new(false);
        internal ManualResetEventSlim AllowCompletion { get; } = new(false);
        internal int CompletionCount;

        public StructuralWriteLease AcquireStructuralWriteLease(InterceptorExecutor executor) =>
            throw new NotSupportedException();

        public Exception? CompleteStructuralWrite(
            InterceptorExecutor executor,
            StructuralWriteLease lease,
            Exception? primaryException) =>
            throw new NotSupportedException();

        public OwnershipReservationToken AcquireOwnershipReservation(
            InterceptorExecutor executor,
            ReservationMode mode) =>
            throw new NotSupportedException();

        public void CompleteOwnershipReservation(
            InterceptorExecutor executor,
            OwnershipReservationToken token,
            bool retainCommittedOwnership)
        {
            Interlocked.Increment(ref CompletionCount);
            CompletionEntered.Set();
            AllowCompletion.Wait();
            executor.ReleaseOwnershipReservation(token, detachIfLast: !retainCommittedOwnership);
        }
    }

    private static (StructuralHolder Subject, InterceptorExecutor Executor) CreateSubject()
    {
        var subject = new StructuralHolder();
        return (subject, (InterceptorExecutor)((IInterceptorSubject)subject).Executor);
    }

    private static InterceptorSubjectContext CreateContext()
    {
        return (InterceptorSubjectContext)InterceptorSubjectContext.Create();
    }

    [Fact]
    public void WhenSameContextAcquiresSharedReservations_ThenParticipantsReleaseIndependently()
    {
        // Arrange
        var (_, executor) = CreateSubject();
        var context = CreateContext();

        // Act
        var first = executor.TryAcquireOwnershipReservation(context, ReservationMode.Shared);
        var second = executor.TryAcquireOwnershipReservation(context, ReservationMode.Shared);

        // Assert
        Assert.NotSame(first, second);
        Assert.Same(first.Reservation, second.Reservation);
        Assert.Equal(2, first.Reservation.ParticipantCount);

        first.Dispose();
        first.Dispose();
        Assert.Equal(1, first.Reservation.ParticipantCount);

        second.Dispose();
        Assert.Equal(0, first.Reservation.ParticipantCount);
    }

    [Fact]
    public void WhenForeignContextCompetesForReservation_ThenItFailsPromptly()
    {
        // Arrange
        var (_, executor) = CreateSubject();
        var firstContext = CreateContext();
        var secondContext = CreateContext();
        using var reservation = executor.TryAcquireOwnershipReservation(firstContext, ReservationMode.Shared);

        // Act & Assert
        Assert.Throws<LifecycleConflictException>(() =>
            executor.TryAcquireOwnershipReservation(secondContext, ReservationMode.Shared));
        Assert.Equal(1, reservation.Reservation.ParticipantCount);
    }

    [Fact]
    public void WhenExclusiveReservationIsActive_ThenAnotherParticipantFailsPromptly()
    {
        // Arrange
        var (_, executor) = CreateSubject();
        var context = CreateContext();
        using var reservation = executor.TryAcquireOwnershipReservation(context, ReservationMode.Exclusive);

        // Act & Assert
        Assert.Throws<LifecycleConflictException>(() =>
            executor.TryAcquireOwnershipReservation(context, ReservationMode.Shared));
        Assert.Throws<LifecycleConflictException>(() =>
            executor.TryAcquireOwnershipReservation(context, ReservationMode.Exclusive));
        Assert.Equal(1, reservation.Reservation.ParticipantCount);
    }

    [Fact]
    public void WhenExclusiveReservationIsActive_ThenStructuralLeaseFailsPromptly()
    {
        // Arrange
        var (_, executor) = CreateSubject();
        var context = CreateContext();
        using var reservation = executor.TryAcquireOwnershipReservation(context, ReservationMode.Exclusive);

        // Act & Assert
        Assert.Throws<LifecycleConflictException>(() => executor.TryAcquireStructuralWriteLease());
    }

    [Fact]
    public void WhenDetachedSubjectHasSharedReservation_ThenStructuralLeaseFailsPromptly()
    {
        // Arrange
        var (_, executor) = CreateSubject();
        var context = CreateContext();
        using var reservation = executor.TryAcquireOwnershipReservation(context, ReservationMode.Shared);

        // Act & Assert
        Assert.Throws<LifecycleConflictException>(() => executor.TryAcquireStructuralWriteLease());
    }

    [Fact]
    public void WhenDetachedSubjectHasActiveStructuralLease_ThenSharedReservationFailsPromptly()
    {
        // Arrange
        var (_, executor) = CreateSubject();
        var context = CreateContext();
        using var lease = executor.TryAcquireStructuralWriteLease();

        // Act & Assert
        Assert.Throws<LifecycleConflictException>(() =>
            executor.TryAcquireOwnershipReservation(context, ReservationMode.Shared));
    }

    [Fact]
    public void WhenAttachedSubjectHasSharedReservation_ThenStructuralLeaseRemainsAvailable()
    {
        // Arrange
        var (_, executor) = CreateSubject();
        var context = CreateContext();
        Assert.True(executor.TryUpdateAttachment(
            0,
            context,
            SubjectAttachmentAnchorKind.Explicit,
            out _));
        using var reservation = executor.TryAcquireOwnershipReservation(context, ReservationMode.Shared);

        // Act
        using var lease = executor.TryAcquireStructuralWriteLease(context);

        // Assert
        Assert.Same(context, lease.Context);
    }

    [Fact]
    public void WhenDetachedSubjectHasSharedReservation_ThenMetadataPublicationFailsPromptly()
    {
        // Arrange
        var (subject, executor) = CreateSubject();
        var context = CreateContext();
        using var reservation = executor.TryAcquireOwnershipReservation(context, ReservationMode.Shared);
        var metadata = new SubjectPropertyMetadata(
            "Late", typeof(int), [], _ => 1, null, isIntercepted: true, isDynamic: true);

        // Act & Assert
        Assert.Throws<LifecycleConflictException>(() =>
            ((IInterceptorSubject)subject).AddProperties([metadata]));
        Assert.False(((IInterceptorSubject)subject).Properties.ContainsKey("Late"));
    }

    [Fact]
    public void WhenDetachedSubjectIsReserved_ThenReservationContextRemainsInvisible()
    {
        // Arrange
        var (subject, executor) = CreateSubject();
        var context = CreateContext();

        // Act
        using var reservation = executor.TryAcquireOwnershipReservation(context, ReservationMode.Shared);
        var attached = executor.TryGetAttachment(out var attachedContext, out var anchor, out var revision);

        // Assert
        Assert.Null(((IInterceptorSubject)subject).TryGetContext());
        Assert.False(attached);
        Assert.Null(attachedContext);
        Assert.Equal(SubjectAttachmentAnchorKind.None, anchor);
        Assert.Equal(0, revision);
    }

    [Fact]
    public void WhenRawAttachmentUpdateRacesReservation_ThenItCannotBypassTheReservation()
    {
        // Arrange
        var (_, executor) = CreateSubject();
        var context = CreateContext();
        using var reservation = executor.TryAcquireOwnershipReservation(context, ReservationMode.Shared);

        // Act & Assert
        Assert.Throws<LifecycleConflictException>(() => executor.TryUpdateAttachment(
            0,
            context,
            SubjectAttachmentAnchorKind.Explicit,
            out _));
        Assert.Null(executor.AttachedContext);
        Assert.Equal(0, executor.AttachmentRevision);
    }

    [Fact]
    public void WhenFinalReservationCannotAdvanceAttachmentRevision_ThenItRemainsProtectedAndCanRetry()
    {
        // Arrange
        var (_, executor) = CreateSubject();
        var context = CreateContext();
        var field = typeof(InterceptorExecutor).GetField(
            "_attachment", BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(executor, new InterceptorExecutor.AttachmentState(
            context,
            SubjectAttachmentAnchorKind.None,
            long.MaxValue,
            AttachmentPhase.Stable));
        var reservation = executor.TryAcquireOwnershipReservation(context, ReservationMode.Shared);

        // Act
        var exception = Record.Exception(() => reservation.Complete(retainCommittedOwnership: false));

        // Assert: exhaustion rejects the whole completion before consuming the token, reservation,
        // or attachment epoch. After the test restores one available revision, the same token can
        // complete normally and leave no ownership or attachment residue.
        Assert.IsType<InvalidOperationException>(exception);
        Assert.True(reservation.IsActive(executor));
        Assert.True(executor.HasOwnershipReservation(context));
        Assert.Equal(1, reservation.Reservation.ParticipantCount);
        Assert.Same(context, executor.AttachedContext);
        Assert.Equal(SubjectAttachmentAnchorKind.None, executor.AttachmentAnchor);
        Assert.Equal(long.MaxValue, executor.AttachmentRevision);
        Assert.Equal(AttachmentPhase.Stable, executor.CurrentAttachmentPhase);

        field.SetValue(executor, new InterceptorExecutor.AttachmentState(
            context,
            SubjectAttachmentAnchorKind.None,
            long.MaxValue - 1,
            AttachmentPhase.Stable));

        var cleanupException = Record.Exception(() =>
            reservation.Complete(retainCommittedOwnership: false));
        Assert.Null(cleanupException);
        Assert.False(reservation.IsActive(executor));
        Assert.False(executor.HasOwnershipReservation(context));
        Assert.Equal(0, reservation.Reservation.ParticipantCount);
        Assert.Null(executor.AttachedContext);
        Assert.Equal(long.MaxValue, executor.AttachmentRevision);
        Assert.Equal(AttachmentPhase.Stable, executor.CurrentAttachmentPhase);
    }

    [Fact]
    public async Task WhenReservationCompletesConcurrently_ThenOnlyOneCallerReleasesTheParticipant()
    {
        // Arrange
        var (_, executor) = CreateSubject();
        var context = CreateContext();
        var coordinator = new BlockingReservationCoordinator();
        var reservation = executor.TryAcquireOwnershipReservation(
            context, ReservationMode.Shared, coordinator);
        var secondStarted = new ManualResetEventSlim(false);
        var first = Task.Run(() => reservation.Complete(retainCommittedOwnership: true));
        Task? second = null;

        try
        {
            await AsyncTestHelpers.WaitUntilAsync(() => coordinator.CompletionEntered.IsSet);
            Assert.False(reservation.IsActive(executor));
            second = Task.Run(() =>
            {
                secondStarted.Set();
                reservation.Complete(retainCommittedOwnership: true);
            });
            await AsyncTestHelpers.WaitUntilAsync(() => secondStarted.IsSet);

            // Act
            coordinator.AllowCompletion.Set();
            await Task.WhenAll(first, second);
        }
        finally
        {
            coordinator.AllowCompletion.Set();
            await Task.WhenAll(first, second ?? Task.CompletedTask);
        }

        // Assert
        Assert.Equal(1, coordinator.CompletionCount);
        Assert.Equal(0, reservation.Reservation.ParticipantCount);
        Assert.False(reservation.IsActive(executor));
        Assert.False(executor.HasOwnershipReservation(context));
    }
}
