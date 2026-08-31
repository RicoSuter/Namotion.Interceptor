using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

[Collection(TemporaryRootTraceCollection.Name)]
public class OwnershipReservationProtocolTests
{
    private sealed class GatedExecutorSubject : IInterceptorSubject
    {
        private IInterceptorExecutor? _executor;
        private bool _armed;

        public IInterceptorExecutor Executor => _armed
            ? throw new InvalidOperationException("The executor accessor was called during reservation completion.")
            : InterceptorExecutor.GetOrCreate(ref _executor, this);

        public ConcurrentDictionary<(string? property, string key), object?> Data { get; } = new();

        public IReadOnlyDictionary<string, SubjectPropertyMetadata> Properties { get; } =
            new Dictionary<string, SubjectPropertyMetadata>();

        public void AddProperties(params IEnumerable<SubjectPropertyMetadata> properties) =>
            throw new NotSupportedException();

        internal void Arm() => _armed = true;
    }

    private sealed class RecordingTraceListener : TraceListener
    {
        internal List<string> Messages { get; } = [];

        public override void Write(string? message) => Messages.Add(message ?? string.Empty);

        public override void WriteLine(string? message) => Messages.Add(message ?? string.Empty);
    }

    private sealed class ThrowingTraceListener : TraceListener
    {
        public override void Write(string? message) =>
            throw new InvalidOperationException("trace listener failed");

        public override void WriteLine(string? message) =>
            throw new InvalidOperationException("trace listener failed");
    }

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

    private static void WaitFor(ManualResetEventSlim signal, string phase)
    {
        if (!signal.Wait(WriteProtocolAcceptance.RendezvousTimeout))
        {
            throw new TimeoutException($"Timed out waiting for {phase}.");
        }
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
        var graph = GetGraph(context);
        var dropped = new Person { FirstName = "dropped" };
        var reservation = graph.ReserveForStructuralWrite(
            (InterceptorExecutor)((IInterceptorSubject)dropped).Executor);

        // Act
        graph.ReleaseUnusedReservation(reservation);
        dropped.AttachToContext(foreignContext);

        // Assert
        Assert.Same(foreignContext, dropped.TryGetContext());
    }

    [Fact]
    public void WhenReservationCompletes_ThenTheCoordinatorUsesItsCapturedExecutor()
    {
        // Arrange
        var context = CreateContext();
        var graph = GetGraph(context);
        var subject = new GatedExecutorSubject();
        var reservation = graph.ReserveForStructuralWrite((InterceptorExecutor)subject.Executor);
        subject.Arm();

        // Act
        var exception = Record.Exception(() => graph.ReleaseUnusedReservation(reservation));

        // Assert
        Assert.Null(exception);
    }

    [Fact]
    public void WhenWritingAlreadyOwnedChild_ThenReservationBlocksRawDetachUntilSupportCommits()
    {
        // Arrange
        var context = CreateContext();
        var graph = GetGraph(context);
        var firstParent = new Person(context) { FirstName = "first" };
        var secondParent = new Person(context) { FirstName = "second" };
        var child = new Person { FirstName = "child" };
        firstParent.Father = child;
        var reservation = graph.ReserveForStructuralWrite(
            (InterceptorExecutor)((IInterceptorSubject)child).Executor);
        var executor = ((IInterceptorSubject)child).Executor;
        executor.TryGetAttachment(out _, out _, out var revision);

        // Act
        var detachException = Record.Exception(() => executor.TryUpdateAttachment(
            revision,
            null,
            SubjectAttachmentAnchorKind.None,
            out _));
        try
        {
            secondParent.Mother = child;
        }
        finally
        {
            graph.ReleaseUnusedReservation(reservation);
        }

        // Assert
        Assert.IsAssignableFrom<InvalidOperationException>(detachException);
        Assert.Same(context, child.TryGetContext());
        Assert.Equal(2, child.GetReferenceCount());
    }

    [Fact]
    public void WhenAnchoredRootDetachesWithSharedReservation_ThenFinalParticipantReleasesItOnce()
    {
        // Arrange
        var context = CreateContext();
        var lifecycle = context.TryGetLifecycleInterceptor()!;
        var graph = lifecycle.Graph;
        var root = new Person(context) { FirstName = "root" };
        var executor = (InterceptorExecutor)((IInterceptorSubject)root).Executor;
        var reservation = graph.ReserveForStructuralWrite(executor);
        var detachCount = 0;
        lifecycle.SubjectDetaching += change =>
        {
            if (change.IsContextDetach && ReferenceEquals(change.Subject, root))
            {
                detachCount++;
            }
        };

        // Act
        var provisionalBeforeDetachException = Record.Exception(() =>
            ((IInterceptorSubject)root).AttachToContext(
                context, SubjectAttachmentAnchorKind.Provisional));
        var detachException = Record.Exception(() => root.DetachFromContext(context));
        var provisionalAfterDetachException = Record.Exception(() =>
            ((IInterceptorSubject)root).AttachToContext(
                context, SubjectAttachmentAnchorKind.Provisional));

        // Assert: removing the anchor commits while the exact shared reservation protects the
        // closure. Provisional attach cannot bypass the exclusive epoch boundary on either side
        // of that commit. Completing the final participant runs the one deferred release.
        Assert.IsType<LifecycleConflictException>(provisionalBeforeDetachException);
        Assert.Null(detachException);
        Assert.IsType<LifecycleConflictException>(provisionalAfterDetachException);
        Assert.Same(context, root.TryGetContext());
        Assert.Equal(SubjectAttachmentAnchorKind.None,
            ((IInterceptorSubject)root).Executor.AttachmentAnchor);
        Assert.Equal(0, detachCount);

        graph.ReleaseUnusedReservation(reservation);
        Assert.Null(root.TryGetContext());
        Assert.Equal(AttachmentPhase.Stable, executor.CurrentAttachmentPhase);
        Assert.Equal(1, detachCount);

        var foreignContext = CreateContext();
        root.AttachToContext(foreignContext);
        var staleContextException = Record.Exception(() =>
            ((IInterceptorSubject)root).AttachToContext(
                context, SubjectAttachmentAnchorKind.Provisional));
        Assert.IsAssignableFrom<InvalidOperationException>(staleContextException);
        Assert.Same(foreignContext, root.TryGetContext());
    }

    [Fact]
    public void WhenAnchoredRootDetachesWithExclusiveReservation_ThenItConflictsWithoutChangingAnchor()
    {
        // Arrange
        var context = CreateContext();
        var lifecycle = context.TryGetLifecycleInterceptor()!;
        var graph = lifecycle.Graph;
        var root = new Person(context) { FirstName = "root" };
        var reservation = graph.ReserveForStructuralWrite(
            (InterceptorExecutor)((IInterceptorSubject)root).Executor,
            ReservationMode.Exclusive);

        try
        {
            // Act
            var detachException = Record.Exception(() => root.DetachFromContext(context));

            // Assert
            Assert.IsType<LifecycleConflictException>(detachException);
            Assert.Same(context, root.TryGetContext());
            Assert.Equal(SubjectAttachmentAnchorKind.Provisional,
                ((IInterceptorSubject)root).Executor.AttachmentAnchor);
        }
        finally
        {
            graph.ReleaseUnusedReservation(reservation);
        }
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
        Exception? foreignWorkerException = null;
        Exception? firstForeignException = null;
        Exception? secondForeignException = null;
        IInterceptorSubjectContext? contextBeforeFirstForeignAttempt = null;
        IInterceptorSubjectContext? contextBeforeSecondForeignAttempt = null;

        var firstWriter = new Thread(() =>
        {
            IDisposable? reservation = null;
            try
            {
                reservation = graph.ReserveForStructuralWrite(
                    (InterceptorExecutor)((IInterceptorSubject)child).Executor);
                firstReserved.Set();
                WaitFor(firstForeignAttempted, "the first foreign attempt");
                graph.ReleaseUnusedReservation(reservation);
                reservation = null;
                firstReleased.Set();
                WaitFor(secondForeignAttempted, "the second foreign attempt");
                firstParent.Father = child;
            }
            catch (Exception exception)
            {
                firstReservationException = exception;
            }
            finally
            {
                if (reservation is not null)
                {
                    graph.ReleaseUnusedReservation(reservation);
                }

                firstReserved.Set();
                firstReleased.Set();
            }
        }) { IsBackground = true };

        var secondWriter = new Thread(() =>
        {
            IDisposable? reservation = null;
            try
            {
                WaitFor(firstReserved, "the first reservation");
                reservation = graph.ReserveForStructuralWrite(
                    (InterceptorExecutor)((IInterceptorSubject)child).Executor);
                secondReserved.Set();
                WaitFor(secondForeignAttempted, "the second foreign attempt");
                secondParent.Mother = child;
            }
            catch (Exception exception)
            {
                secondReservationException = exception;
            }
            finally
            {
                if (reservation is not null)
                {
                    graph.ReleaseUnusedReservation(reservation);
                }

                secondReserved.Set();
            }
        }) { IsBackground = true };

        var foreignAttacher = new Thread(() =>
        {
            try
            {
                WaitFor(secondReserved, "the second reservation");
                contextBeforeFirstForeignAttempt = child.TryGetContext();
                firstForeignException = Record.Exception(() => child.AttachToContext(foreignContext));
                firstForeignAttempted.Set();
                WaitFor(firstReleased, "the first participant release");
                contextBeforeSecondForeignAttempt = child.TryGetContext();
                secondForeignException = Record.Exception(() => child.AttachToContext(foreignContext));
            }
            catch (Exception exception)
            {
                foreignWorkerException = exception;
            }
            finally
            {
                firstForeignAttempted.Set();
                secondForeignAttempted.Set();
            }
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
        Assert.Null(foreignWorkerException);
        Assert.Null(contextBeforeFirstForeignAttempt);
        Assert.Null(contextBeforeSecondForeignAttempt);
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
        var reservation = graph.ReserveForStructuralWrite(
            (InterceptorExecutor)((IInterceptorSubject)child).Executor);
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

        Assert.Null(child.TryGetContext());
    }

    [Fact]
    public void WhenDescendantLeaseProtectsAnOrphanedCycle_ThenItsClosureRemainsUntilCompletion()
    {
        // Arrange
        var context = CreateContext();
        var foreignContext = CreateContext();
        var lifecycle = (LifecycleInterceptor)context.TryGetService<ILifecycleInterceptor>()!;
        var root = new Person(context) { FirstName = "root" };
        var first = new Person { FirstName = "first" };
        var protectedSubject = new Person { FirstName = "protected" };
        root.Father = first;
        first.Father = protectedSubject;
        protectedSubject.Father = first;
        var detached = new List<IInterceptorSubject>();
        lifecycle.SubjectDetaching += change =>
        {
            if (change.IsContextDetach)
            {
                detached.Add(change.Subject);
            }
        };
        var lease = ((ITopologyAdmissionCoordinator)lifecycle).AcquireStructuralWriteLease(
            (InterceptorExecutor)((IInterceptorSubject)protectedSubject).Executor);

        // Act
        root.Father = null;
        var foreignException = Record.Exception(() => first.AttachToContext(foreignContext));

        // Assert
        Assert.Null(root.Father);
        Assert.Same(context, first.TryGetContext());
        Assert.Same(context, protectedSubject.TryGetContext());
        Assert.IsAssignableFrom<InvalidOperationException>(foreignException);
        Assert.Empty(detached);

        var completionException = lease.Complete(null);
        Assert.Null(completionException);
        Assert.Null(first.TryGetContext());
        Assert.Null(protectedSubject.TryGetContext());
        Assert.Equal(1, detached.Count(subject => ReferenceEquals(subject, first)));
        Assert.Equal(1, detached.Count(subject => ReferenceEquals(subject, protectedSubject)));
    }

    [Fact]
    public void WhenOverlappingLeasesProtectAClosedCycle_ThenTheFinalLeaseReleasesItOnce()
    {
        // Arrange
        var context = CreateContext();
        var lifecycle = (LifecycleInterceptor)context.TryGetService<ILifecycleInterceptor>()!;
        var root = new Person(context) { FirstName = "root" };
        var first = new Person { FirstName = "first" };
        var second = new Person { FirstName = "second" };
        root.Father = first;
        first.Father = second;
        second.Father = first;
        var detached = new List<IInterceptorSubject>();
        lifecycle.SubjectDetaching += change =>
        {
            if (change.IsContextDetach)
            {
                detached.Add(change.Subject);
            }
        };
        var coordinator = (ITopologyAdmissionCoordinator)lifecycle;
        var firstLease = coordinator.AcquireStructuralWriteLease(
            (InterceptorExecutor)((IInterceptorSubject)first).Executor);
        var secondLease = coordinator.AcquireStructuralWriteLease(
            (InterceptorExecutor)((IInterceptorSubject)second).Executor);
        root.Father = null;

        // Act
        var firstCompletionException = firstLease.Complete(null);

        // Assert
        Assert.Null(firstCompletionException);
        Assert.Same(context, first.TryGetContext());
        Assert.Same(context, second.TryGetContext());
        Assert.Empty(detached);

        var secondCompletionException = secondLease.Complete(null);
        Assert.Null(secondCompletionException);
        Assert.Null(first.TryGetContext());
        Assert.Null(second.TryGetContext());
        Assert.Equal(1, detached.Count(subject => ReferenceEquals(subject, first)));
        Assert.Equal(1, detached.Count(subject => ReferenceEquals(subject, second)));
    }

    [Fact]
    public void WhenNewSupportCommitsBeforeTheFinalProtectorLeaves_ThenTheClosureStaysAttached()
    {
        // Arrange
        var context = CreateContext();
        var lifecycle = (LifecycleInterceptor)context.TryGetService<ILifecycleInterceptor>()!;
        var firstRoot = new Person(context) { FirstName = "first root" };
        var secondRoot = new Person(context) { FirstName = "second root" };
        var first = new Person { FirstName = "first" };
        var second = new Person { FirstName = "second" };
        firstRoot.Father = first;
        first.Father = second;
        second.Father = first;
        var detached = new List<IInterceptorSubject>();
        lifecycle.SubjectDetaching += change =>
        {
            if (change.IsContextDetach)
            {
                detached.Add(change.Subject);
            }
        };
        var lease = ((ITopologyAdmissionCoordinator)lifecycle).AcquireStructuralWriteLease(
            (InterceptorExecutor)((IInterceptorSubject)first).Executor);
        firstRoot.Father = null;

        // Act
        secondRoot.Mother = first;
        var completionException = lease.Complete(null);

        // Assert
        Assert.Null(completionException);
        Assert.Same(context, first.TryGetContext());
        Assert.Same(context, second.TryGetContext());
        Assert.Empty(detached);

        secondRoot.Mother = null;
        Assert.Null(first.TryGetContext());
        Assert.Null(second.TryGetContext());
    }

    [Fact]
    public void WhenDeferredSweepCallbackFails_ThenExplicitCompletionAggregatesThePrimaryException()
    {
        // Arrange
        var context = CreateContext();
        var lifecycle = (LifecycleInterceptor)context.TryGetService<ILifecycleInterceptor>()!;
        var root = new Person(context) { FirstName = "root" };
        var first = new Person { FirstName = "first" };
        var second = new Person { FirstName = "second" };
        root.Father = first;
        first.Father = second;
        second.Father = first;
        var callbackFailure = new InvalidOperationException("deferred sweep callback failed");
        lifecycle.SubjectDetaching += change =>
        {
            if (change.IsContextDetach && ReferenceEquals(change.Subject, first))
            {
                throw callbackFailure;
            }
        };
        var lease = ((ITopologyAdmissionCoordinator)lifecycle).AcquireStructuralWriteLease(
            (InterceptorExecutor)((IInterceptorSubject)first).Executor);
        root.Father = null;
        var primaryFailure = new InvalidOperationException("primary chain failed");

        // Act
        var completionException = lease.Complete(primaryFailure);

        // Assert
        var aggregate = Assert.IsType<AggregateException>(completionException);
        Assert.Collection(
            aggregate.InnerExceptions,
            exception => Assert.Same(primaryFailure, exception),
            exception => Assert.Same(callbackFailure, exception));
        Assert.Null(first.TryGetContext());
        Assert.Null(second.TryGetContext());
    }

    [Fact]
    public void WhenDeferredSweepJournalCompletionFails_ThenPreparedReleaseDoesNotPublish()
    {
        // Arrange
        var context = CreateContext();
        var lifecycle = (LifecycleInterceptor)context.TryGetService<ILifecycleInterceptor>()!;
        var root = new Person(context) { FirstName = "root" };
        var first = new Person { FirstName = "first" };
        var second = new Person { FirstName = "second" };
        root.Father = first;
        first.Father = second;
        second.Father = first;
        var lease = ((ITopologyAdmissionCoordinator)lifecycle).AcquireStructuralWriteLease(
            (InterceptorExecutor)((IInterceptorSubject)first).Executor);
        root.Father = null;
        var completionFailure = new InvalidOperationException("journal completion failed");
        lifecycle.FailNextJournalCompletionForTests(completionFailure);

        // Act
        var exception = Record.Exception(() => lease.Complete(null));

        // Assert
        Assert.Same(completionFailure, exception);
        Assert.Same(context, first.TryGetContext());
        Assert.Same(context, second.TryGetContext());
        Assert.Equal(1, first.GetReferenceCount());
        Assert.Equal(1, second.GetReferenceCount());
    }

    [Fact]
    public void WhenDeferredSweepCallbackFailsDuringFallbackDisposal_ThenFailureIsTracedWithoutThrowing()
    {
        // Arrange
        var context = CreateContext();
        var lifecycle = (LifecycleInterceptor)context.TryGetService<ILifecycleInterceptor>()!;
        var root = new Person(context) { FirstName = "root" };
        var first = new Person { FirstName = "first" };
        var second = new Person { FirstName = "second" };
        root.Father = first;
        first.Father = second;
        second.Father = first;
        lifecycle.SubjectDetaching += change =>
        {
            if (change.IsContextDetach && ReferenceEquals(change.Subject, first))
            {
                throw new InvalidOperationException("fallback sweep callback failed");
            }
        };
        var lease = ((ITopologyAdmissionCoordinator)lifecycle).AcquireStructuralWriteLease(
            (InterceptorExecutor)((IInterceptorSubject)first).Executor);
        root.Father = null;
        var listener = new RecordingTraceListener();
        Trace.Listeners.Add(listener);

        try
        {
            // Act
            var disposalException = Record.Exception(lease.Dispose);

            // Assert
            Assert.Null(disposalException);
            Assert.Contains(listener.Messages, message => message.Contains("fallback sweep callback failed"));
            Assert.Null(first.TryGetContext());
            Assert.Null(second.TryGetContext());
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }
    }

    [Fact]
    public void WhenWithheldRecalculationAndTraceListenerThrow_ThenDiagnosticDrainDoesNotThrow()
    {
        // Arrange: this directly exercises the Task 6 compatibility drain. Its diagnostics are a
        // no-throw boundary even when both the deferred recalculation and Trace infrastructure fail.
        var context = CreateContext();
        var lifecycle = context.TryGetLifecycleInterceptor()!;
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(LifecycleInterceptor).GetField("_withheldRecalculations", flags)!.SetValue(
            lifecycle,
            new List<Action> { () => throw new InvalidOperationException("recalculation failed") });
        var drain = typeof(LifecycleInterceptor).GetMethod("RunWithheldRecalculations", flags)!;
        var listener = new ThrowingTraceListener();
        Trace.Listeners.Add(listener);

        try
        {
            // Act
            var exception = Record.Exception(() => drain.Invoke(lifecycle, null));

            // Assert
            Assert.Null(exception);
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TemporaryRootTraceCollection
{
    public const string Name = "Temporary root trace";
}
