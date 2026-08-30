using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Testing;

namespace Namotion.Interceptor.Tests.Interceptors;

public class OwnershipReservationTests
{
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
    public void WhenExclusiveReservationOwnsAttachmentTransition_ThenItsTokenCanCommit()
    {
        // Arrange
        var (_, executor) = CreateSubject();
        var context = CreateContext();
        using var reservation = executor.TryAcquireOwnershipReservation(context, ReservationMode.Exclusive);

        // Act
        var success = reservation.TryUpdateAttachment(
            0,
            context,
            SubjectAttachmentAnchorKind.Explicit,
            out var revision);

        // Assert
        Assert.True(success);
        Assert.Equal(1, revision);
        Assert.Same(context, executor.AttachedContext);
        Assert.Equal(SubjectAttachmentAnchorKind.Explicit, executor.AttachmentAnchor);
    }

    [Fact]
    public void WhenProvisionalAttachmentIsReservedExclusively_ThenItsTokenCanPromoteItToExplicit()
    {
        // Arrange
        var (_, executor) = CreateSubject();
        var context = CreateContext();
        Assert.True(executor.TryUpdateAttachment(
            0,
            context,
            SubjectAttachmentAnchorKind.Provisional,
            out var provisionalRevision));
        using var reservation = executor.TryAcquireOwnershipReservation(context, ReservationMode.Exclusive);

        // Act
        var success = reservation.TryUpdateAttachment(
            provisionalRevision,
            context,
            SubjectAttachmentAnchorKind.Explicit,
            out var explicitRevision);

        // Assert
        Assert.True(success);
        Assert.Equal(provisionalRevision + 1, explicitRevision);
        Assert.Same(context, executor.AttachedContext);
        Assert.Equal(SubjectAttachmentAnchorKind.Explicit, executor.AttachmentAnchor);
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
    public void WhenSharedReservationCommitsAttachment_ThenParticipantDisposalDoesNotDetachIt()
    {
        // Arrange
        var (_, executor) = CreateSubject();
        var context = CreateContext();
        var first = executor.TryAcquireOwnershipReservation(context, ReservationMode.Shared);
        var second = executor.TryAcquireOwnershipReservation(context, ReservationMode.Shared);

        // Act
        Assert.True(first.TryUpdateAttachment(
            0,
            context,
            SubjectAttachmentAnchorKind.None,
            out var revision));
        first.Dispose();
        second.Dispose();

        // Assert
        Assert.Same(context, executor.AttachedContext);
        Assert.Equal(SubjectAttachmentAnchorKind.None, executor.AttachmentAnchor);
        Assert.Equal(1, revision);
        Assert.Equal(0, first.Reservation.ParticipantCount);
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
}
