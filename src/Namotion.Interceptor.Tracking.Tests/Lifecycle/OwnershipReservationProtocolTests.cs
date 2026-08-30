using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

public class OwnershipReservationProtocolTests
{
    [RunsAfter(typeof(LifecycleInterceptor))]
    private sealed class ReservationVisibilityInterceptor(Action observe) : IWriteInterceptor
    {
        public void WriteProperty<TProperty>(
            ref PropertyWriteContext<TProperty> context,
            WriteInterceptionDelegate<TProperty> next)
        {
            observe();
            next(ref context);
        }
    }

    private static IInterceptorSubjectContext CreateContext()
    {
        return InterceptorSubjectContext
            .Create()
            .WithLifecycle();
    }

    private static OwnershipGraph GetGraph(IInterceptorSubjectContext context)
    {
        return ((LifecycleInterceptor)context.TryGetService<ILifecycleInterceptor>()!).Graph;
    }

    [Fact]
    public void WhenStructuralWriteReachesDownstreamTerminal_ThenProposedChildReservationIsNotAttachment()
    {
        // Arrange
        IInterceptorSubjectContext? contextObservedBeforeTerminal = null;
        var child = new Person { FirstName = "child" };
        var observer = new ReservationVisibilityInterceptor(
            () => contextObservedBeforeTerminal = child.TryGetContext());
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle()
            .WithService(() => observer, _ => false);
        var parent = new Person(context) { FirstName = "parent" };

        // Act
        parent.Father = child;

        // Assert
        Assert.Null(contextObservedBeforeTerminal);
        Assert.Same(context, child.TryGetContext());
    }

    [Fact]
    public void WhenFinalReservationCommittedNoOwnership_ThenReleaseUnusedReservationHandsSubjectBack()
    {
        // Arrange
        var context = CreateContext();
        var foreignContext = CreateContext();
        var parent = new ReorderingDevice();
        ((IInterceptorSubject)parent).AttachToContext(context);
        var kept = new ReorderingDevice { Name = "kept" };
        var dropped = new ReorderingDevice { Name = "dropped" };

        // Act
        parent.Children = [kept, dropped];
        ((IInterceptorSubject)dropped).AttachToContext(foreignContext);

        // Assert
        Assert.Same(foreignContext, ((IInterceptorSubject)dropped).TryGetContext());
    }

    [Fact]
    public void WhenWritingAlreadyOwnedChild_ThenReservationBlocksRawDetachUntilSupportCommits()
    {
        // Arrange
        var child = new Person { FirstName = "child" };
        var attemptDetach = false;
        Exception? detachException = null;
        var observer = new ReservationVisibilityInterceptor(() =>
        {
            if (!attemptDetach)
            {
                return;
            }

            var executor = ((IInterceptorSubject)child).Executor;
            executor.TryGetAttachment(out _, out _, out var revision);
            detachException = Record.Exception(() => executor.TryUpdateAttachment(
                revision,
                null,
                SubjectAttachmentAnchorKind.None,
                out _));
        });
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle()
            .WithService(() => observer, _ => false);
        var firstParent = new Person(context) { FirstName = "first" };
        var secondParent = new Person(context) { FirstName = "second" };
        firstParent.Father = child;
        attemptDetach = true;

        // Act
        secondParent.Mother = child;

        // Assert
        Assert.IsAssignableFrom<InvalidOperationException>(detachException);
        Assert.Same(context, child.TryGetContext());
        Assert.Equal(2, child.GetReferenceCount());
    }

    [Fact]
    [Trait("Category", "Concurrency")]
    public void WhenTwoSameContextWritesReserveDetachedChild_ThenForeignAttachCannotEnterAClaimGap()
    {
        // Arrange
        var context = CreateContext();
        var foreignContext = CreateContext();
        var graph = GetGraph(context);
        var child = new Person { FirstName = "child" };
        var firstParent = new Person(context) { FirstName = "first" };
        var secondParent = new Person(context) { FirstName = "second" };
        var firstReserved = new ManualResetEventSlim(false);
        var secondReserved = new ManualResetEventSlim(false);
        var firstForeignAttempted = new ManualResetEventSlim(false);
        var firstReleased = new ManualResetEventSlim(false);
        var secondForeignAttempted = new ManualResetEventSlim(false);
        Exception? firstReservationException = null;
        Exception? secondReservationException = null;
        Exception? firstForeignException = null;
        Exception? secondForeignException = null;

        var firstWriter = new Thread(() =>
        {
            try
            {
                var reservation = graph.ReserveForStructuralWrite(child);
                try
                {
                    firstReserved.Set();
                    firstForeignAttempted.Wait(WriteProtocolAcceptance.RendezvousTimeout);
                    firstParent.Father = child;
                }
                finally
                {
                    graph.ReleaseUnusedReservation(reservation);
                }
            }
            catch (Exception exception)
            {
                firstReservationException = exception;
                firstReserved.Set();
            }
            finally
            {
                firstReleased.Set();
            }
        }) { IsBackground = true };

        var secondWriter = new Thread(() =>
        {
            try
            {
                firstReserved.Wait(WriteProtocolAcceptance.RendezvousTimeout);
                var reservation = graph.ReserveForStructuralWrite(child);
                try
                {
                    secondReserved.Set();
                    secondForeignAttempted.Wait(WriteProtocolAcceptance.RendezvousTimeout);
                    secondParent.Mother = child;
                }
                finally
                {
                    graph.ReleaseUnusedReservation(reservation);
                }
            }
            catch (Exception exception)
            {
                secondReservationException = exception;
                secondReserved.Set();
            }
        }) { IsBackground = true };

        var foreignAttacher = new Thread(() =>
        {
            secondReserved.Wait(WriteProtocolAcceptance.RendezvousTimeout);
            firstForeignException = Record.Exception(() => child.AttachToContext(foreignContext));
            firstForeignAttempted.Set();
            firstReleased.Wait(WriteProtocolAcceptance.RendezvousTimeout);
            secondForeignException = Record.Exception(() => child.AttachToContext(foreignContext));
            secondForeignAttempted.Set();
        }) { IsBackground = true };

        // Act
        firstWriter.Start();
        secondWriter.Start();
        foreignAttacher.Start();
        var firstCompleted = firstWriter.Join(WriteProtocolAcceptance.RendezvousTimeout);
        var secondCompleted = secondWriter.Join(WriteProtocolAcceptance.RendezvousTimeout);
        var foreignCompleted = foreignAttacher.Join(WriteProtocolAcceptance.RendezvousTimeout);

        // Assert
        Assert.True(firstCompleted, "the first writer never finished");
        Assert.True(secondCompleted, "the second writer never finished");
        Assert.True(foreignCompleted, "the foreign attacher never finished");
        Assert.Null(firstReservationException);
        Assert.Null(secondReservationException);
        Assert.IsAssignableFrom<InvalidOperationException>(firstForeignException);
        Assert.IsAssignableFrom<InvalidOperationException>(secondForeignException);
        Assert.Same(context, child.TryGetContext());
        Assert.Same(child, firstParent.Father);
        Assert.Same(child, secondParent.Mother);
        Assert.Equal(2, child.GetReferenceCount());
    }

    [Fact]
    [Trait("Category", "Concurrency")]
    public void WhenReservedOwnedChildLosesItsLastCommittedEdge_ThenItRemainsProtectedFromForeignClaim()
    {
        // Arrange
        var context = CreateContext();
        var foreignContext = CreateContext();
        var parent = new Person(context) { FirstName = "parent" };
        var child = new Person { FirstName = "child" };
        parent.Children = [child];
        var graph = GetGraph(context);
        var reservation = graph.ReserveForStructuralWrite(child);
        try
        {
            // Act
            parent.Children = [];
            var foreignException = Record.Exception(() => child.AttachToContext(foreignContext));

            // Assert
            Assert.Same(context, child.TryGetContext());
            Assert.IsAssignableFrom<InvalidOperationException>(foreignException);
        }
        finally
        {
            graph.ReleaseUnusedReservation(reservation);
        }
    }
}
