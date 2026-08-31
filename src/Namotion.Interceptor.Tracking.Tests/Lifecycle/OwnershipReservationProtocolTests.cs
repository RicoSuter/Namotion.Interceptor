using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Testing;
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

    private sealed class AlwaysAdmittedWriteCommitGuard : IWriteCommitGuard
    {
        public bool TryEnter() => true;

        public void Exit()
        {
        }

        public bool TryDefer() => false;

        public void Resume()
        {
        }
    }

    private sealed class BlockingScalarWriteInterceptor : IWriteInterceptor, IDisposable
    {
        private IInterceptorSubject? _subject;
        private string? _propertyName;
        private int _isArmed;

        internal ManualResetEventSlim WriteEntered { get; } = new(false);
        internal ManualResetEventSlim ContinueWrite { get; } = new(false);

        internal void Arm(IInterceptorSubject subject, string propertyName)
        {
            _subject = subject;
            _propertyName = propertyName;
            Volatile.Write(ref _isArmed, 1);
        }

        public void WriteProperty<TProperty>(
            ref PropertyWriteContext<TProperty> context,
            WriteInterceptionDelegate<TProperty> next)
        {
            if (ReferenceEquals(context.Property.Subject, _subject) &&
                context.Property.Name == _propertyName &&
                Interlocked.Exchange(ref _isArmed, 0) == 1)
            {
                WriteEntered.Set();
                WaitFor(ContinueWrite, "the scalar callback write to resume");
            }

            next(ref context);
        }

        public void Dispose()
        {
            WriteEntered.Dispose();
            ContinueWrite.Dispose();
        }
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
    public void WhenExclusiveReservationOwnsSubject_ThenScalarWriteRejectsBeforeInterceptorsRun()
    {
        // Arrange
        var interceptorCalls = 0;
        var isArmed = false;
        var observer = new ReservationVisibilityInterceptor(() =>
        {
            if (isArmed)
            {
                Interlocked.Increment(ref interceptorCalls);
            }
        });
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle()
            .WithService(() => observer, _ => false);
        var subject = new Person(context) { FirstName = "before" };
        var executor = (InterceptorExecutor)((IInterceptorSubject)subject).Executor;
        using var reservation = GetGraph(context).ReserveForStructuralWrite(
            executor,
            ReservationMode.Exclusive);
        isArmed = true;

        // Act
        var exception = Record.Exception(() => subject.FirstName = "after");

        // Assert
        Assert.IsType<LifecycleConflictException>(exception);
        Assert.Equal(0, interceptorCalls);
        Assert.Equal("before", subject.FirstName);
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

        // Assert: a stable same-context provisional request is a cheap no-op even while the Shared
        // reservation protects the exact epoch. Removing the anchor commits while that reservation
        // retains the closure, and the same no-op leaves the unanchored subject alone. Completing
        // the final participant then runs the one deferred release.
        Assert.Null(provisionalBeforeDetachException);
        Assert.Null(detachException);
        Assert.Null(provisionalAfterDetachException);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhenStableProvisionalAttachHasAnActiveProtector_ThenItSucceedsWithoutRecapturing(
        bool useSharedReservation)
    {
        // Arrange
        var context = CreateContext();
        var lifecycle = context.TryGetLifecycleInterceptor()!;
        var root = new Person(context) { FirstName = "root" };
        var executor = (InterceptorExecutor)((IInterceptorSubject)root).Executor;
        var getterReads = 0;
        ((IInterceptorSubject)root).AddProperties(new SubjectPropertyMetadata(
            "NoOpChild",
            typeof(Person),
            [],
            _ =>
            {
                getterReads++;
                return null;
            },
            null,
            isIntercepted: true,
            isDynamic: true));
        var readsBeforeNoOp = getterReads;
        using IDisposable protector = useSharedReservation
            ? lifecycle.Graph.ReserveForStructuralWrite(executor)
            : executor.TryAcquireStructuralWriteLease((InterceptorSubjectContext)context);

        // Act
        var exception = Record.Exception(() =>
            ((IInterceptorSubject)root).AttachToContext(
                context, SubjectAttachmentAnchorKind.Provisional));

        // Assert
        Assert.Null(exception);
        Assert.Equal(readsBeforeNoOp, getterReads);
        Assert.Same(context, root.TryGetContext());
        Assert.Equal(SubjectAttachmentAnchorKind.Provisional, executor.AttachmentAnchor);
    }

    [Fact]
    [Trait("Category", "Concurrency")]
    public void WhenProvisionalCaptureLosesToExplicitAttach_ThenItDoesNotDemoteTheAnchor()
    {
        // Arrange
        var context = CreateContext();
        var lifecycle = context.TryGetLifecycleInterceptor()!;
        var root = new Person { FirstName = "root" };
        var captureEntered = new ManualResetEventSlim(false);
        var allowCapture = new ManualResetEventSlim(false);
        var captureClaimed = 0;
        ((IInterceptorSubject)root).AddProperties(new SubjectPropertyMetadata(
            "CaptureGate",
            typeof(Person),
            [],
            _ =>
            {
                if (Interlocked.CompareExchange(ref captureClaimed, 1, 0) == 0)
                {
                    captureEntered.Set();
                    WaitFor(allowCapture, "the provisional capture to resume");
                }

                return null;
            },
            null,
            isIntercepted: true,
            isDynamic: true));
        var attachCount = 0;
        lifecycle.SubjectAttached += change =>
        {
            if (ReferenceEquals(change.Subject, root))
            {
                Interlocked.Increment(ref attachCount);
            }
        };
        Exception? provisionalException = null;
        var provisional = new Thread(() =>
        {
            provisionalException = Record.Exception(() =>
                ((IInterceptorSubject)root).AttachToContext(
                    context, SubjectAttachmentAnchorKind.Provisional));
        }) { IsBackground = true };

        // Act
        provisional.Start();
        WaitFor(captureEntered, "the provisional stable-null capture");
        var explicitException = Record.Exception(() => root.AttachToContext(context));
        allowCapture.Set();
        var provisionalCompleted = provisional.Join(WriteProtocolAcceptance.RendezvousTimeout);

        // Assert
        Assert.True(provisionalCompleted);
        Assert.Null(explicitException);
        Assert.Null(provisionalException);
        Assert.Same(context, root.TryGetContext());
        Assert.Equal(SubjectAttachmentAnchorKind.Explicit,
            ((IInterceptorSubject)root).Executor.AttachmentAnchor);
        Assert.True(lifecycle.Graph.IsOwned(root));
        Assert.Equal(1, attachCount);
    }

    [Fact]
    [Trait("Category", "Concurrency")]
    public void WhenReservationCompletionDrainsDetachCallbacks_ThenConcurrentDisposeDoesNotWaitForATokenMonitor()
    {
        // Arrange
        var context = CreateContext();
        var lifecycle = context.TryGetLifecycleInterceptor()!;
        var root = new Person(context) { FirstName = "root" };
        var executor = (InterceptorExecutor)((IInterceptorSubject)root).Executor;
        var reservation = (OwnershipReservationToken)lifecycle.Graph.ReserveForStructuralWrite(executor);
        var workerStarted = new ManualResetEventSlim(false);
        var workerCompleted = new ManualResetEventSlim(false);
        var tokenMonitorHeld = false;
        lifecycle.SubjectDetaching += change =>
        {
            if (change.IsContextDetach && ReferenceEquals(change.Subject, root))
            {
                var concurrentDispose = new Thread(() =>
                {
                    workerStarted.Set();
                    reservation.Dispose();
                    workerCompleted.Set();
                }) { IsBackground = true };
                concurrentDispose.Start();
                WaitFor(workerStarted, "the concurrent reservation disposal");
                tokenMonitorHeld = Monitor.IsEntered(reservation);
                if (!tokenMonitorHeld)
                {
                    WaitFor(workerCompleted, "the concurrent reservation disposal to complete");
                }
            }
        };

        // Act
        root.DetachFromContext(context);
        lifecycle.Graph.ReleaseUnusedReservation(reservation);
        WaitFor(workerCompleted, "the concurrent reservation disposal after callback drain");

        // Assert
        Assert.False(tokenMonitorHeld);
        Assert.Equal(0, reservation.Reservation.ParticipantCount);
        Assert.False(reservation.IsActive(executor));
        Assert.Null(root.TryGetContext());
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
        var secondWriteCompleted = new ManualResetEventSlim(false);
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
                // The reservation handoff is the race under test. Concurrent attachment
                // publication may reject one writer at its documented non-stable boundary.
                WaitFor(secondWriteCompleted, "the second structural write");
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
                secondWriteCompleted.Set();
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
                secondWriteCompleted.Set();
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
    [Trait("Category", "Concurrency")]
    public void WhenAttachedScalarWriteIsActive_ThenSharedReservationCanOverlapIt()
    {
        // Arrange
        using var blocker = new BlockingScalarWriteInterceptor();
        var context = CreateContext()
            .WithService(() => blocker, _ => false);
        var graph = GetGraph(context);
        var subject = new Person(context) { FirstName = "initial" };
        var executor = (InterceptorExecutor)((IInterceptorSubject)subject).Executor;
        blocker.Arm(subject, nameof(Person.FirstName));
        Exception? writerException = null;
        var writer = new Thread(
            () => writerException = Record.Exception(() => subject.FirstName = "updated"))
        {
            IsBackground = true
        };

        // Act
        writer.Start();
        WaitFor(blocker.WriteEntered, "the attached scalar interceptor");
        IDisposable? reservation = null;
        var reservationException = Record.Exception(
            () => reservation = graph.ReserveForStructuralWrite(executor));
        blocker.ContinueWrite.Set();
        var writerCompleted = writer.Join(WriteProtocolAcceptance.RendezvousTimeout);
        reservation?.Dispose();

        // Assert
        Assert.True(writerCompleted, "the scalar writer did not complete");
        Assert.Null(writerException);
        Assert.Null(reservationException);
        Assert.Equal("updated", subject.FirstName);
    }

    [Fact]
    [Trait("Category", "Concurrency")]
    public void WhenDeferredSweepMeetsScalarRawCommit_ThenOrphanStillDetachesAfterCommit()
    {
        // Arrange
        var context = CreateContext();
        var graph = GetGraph(context);
        var root = new Person(context) { FirstName = "root" };
        var child = new Person { FirstName = "child" };
        root.Father = child;
        var executor = (InterceptorExecutor)((IInterceptorSubject)child).Executor;
        var reservation = graph.ReserveForStructuralWrite(executor);
        root.Father = null;
        var rawCommitEntered = new ManualResetEventSlim(false);
        var continueRawCommit = new ManualResetEventSlim(false);
        Exception? writerException = null;
        var writer = new Thread(() =>
        {
            writerException = Record.Exception(() => executor.SetPropertyValue(
                nameof(Person.FirstName),
                "updated",
                child.FirstName,
                (_, _) =>
                {
                    rawCommitEntered.Set();
                    WaitFor(continueRawCommit, "the scalar raw commit to resume");
                }));
        }) { IsBackground = true };

        // Act
        writer.Start();
        WaitFor(rawCommitEntered, "the scalar raw commit");
        Exception? completionException;
        try
        {
            completionException = Record.Exception(() => graph.ReleaseUnusedReservation(reservation));
        }
        finally
        {
            continueRawCommit.Set();
        }

        var writerCompleted = writer.Join(WriteProtocolAcceptance.RendezvousTimeout);

        // Assert
        Assert.True(writerCompleted, "the scalar writer never finished");
        Assert.Null(writerException);
        Assert.Null(completionException);
        Assert.Null(child.TryGetContext());
        Assert.False(graph.IsOwned(child));
    }

    [Fact]
    [Trait("Category", "Concurrency")]
    public void WhenDeferredSweepMeetsDerivedNotificationRawCommit_ThenOrphanStillDetachesAfterCommit()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();
        var graph = GetGraph(context);
        var root = new Person(context) { FirstName = "root" };
        var child = new Person { FirstName = "child" };
        root.Father = child;
        var executor = (InterceptorExecutor)((IInterceptorSubject)child).Executor;
        var reservation = graph.ReserveForStructuralWrite(executor);
        root.Father = null;
        var rawCommitEntered = new ManualResetEventSlim(false);
        var continueRawCommit = new ManualResetEventSlim(false);
        Exception? writerException = null;
        var writer = new Thread(() =>
        {
            writerException = Record.Exception(() => executor.SetDeferredPropertyValue(
                nameof(Person.FullName),
                child.FullName,
                "previous derived value",
                (_, _) =>
                {
                    rawCommitEntered.Set();
                    WaitFor(continueRawCommit, "the derived notification raw commit to resume");
                },
                DateTimeOffset.UtcNow.Ticks,
                new AlwaysAdmittedWriteCommitGuard()));
        }) { IsBackground = true };

        // Act
        writer.Start();
        WaitFor(rawCommitEntered, "the derived notification raw commit");
        Exception? completionException;
        try
        {
            completionException = Record.Exception(() => graph.ReleaseUnusedReservation(reservation));
        }
        finally
        {
            continueRawCommit.Set();
        }

        var writerCompleted = writer.Join(WriteProtocolAcceptance.RendezvousTimeout);

        // Assert
        Assert.True(writerCompleted, "the derived notification writer never finished");
        Assert.Null(writerException);
        Assert.Null(completionException);
        Assert.Null(child.TryGetContext());
        Assert.False(graph.IsOwned(child));
    }

    [Fact]
    [Trait("Category", "Concurrency")]
    public async Task WhenSameContextCallbackCompletesScalarWriteDuringDeferredSweep_ThenCallbacksDoNotNestAndOrphanDetaches()
    {
        // Arrange
        using var blocker = new BlockingScalarWriteInterceptor();
        using var callbackDepth = new ThreadLocal<int>(() => 0);
        using var childDetached = new ManualResetEventSlim(false);
        Person? child = null;
        Person? trigger = null;
        var nestedCallbackCount = 0;
        var handler = new DelegateLifecycleHandler(change =>
        {
            if (callbackDepth.Value != 0)
            {
                Interlocked.Increment(ref nestedCallbackCount);
            }

            callbackDepth.Value++;
            try
            {
                if (change.IsContextAttach && ReferenceEquals(change.Subject, trigger))
                {
                    child!.FirstName = "updated";
                }

                if (change.IsContextDetach && ReferenceEquals(change.Subject, child))
                {
                    childDetached.Set();
                }
            }
            finally
            {
                callbackDepth.Value--;
            }
        });
        var context = CreateContext()
            .WithService(() => blocker, _ => false)
            .WithService(() => handler, _ => false);
        var graph = GetGraph(context);
        var root = new Person(context) { FirstName = "root" };
        child = new Person { FirstName = "child" };
        root.Father = child;
        var reservation = graph.ReserveForStructuralWrite(
            (InterceptorExecutor)((IInterceptorSubject)child).Executor);
        root.Father = null;
        trigger = new Person { FirstName = "trigger" };
        blocker.Arm(child, nameof(Person.FirstName));
        Exception? triggerException = null;
        var triggerThread = new Thread(
            () => triggerException = Record.Exception(() => trigger.AttachToContext(context)))
        {
            IsBackground = true
        };

        // Act
        triggerThread.Start();
        WaitFor(blocker.WriteEntered, "the same-context callback scalar write");
        var completionException = Record.Exception(() => graph.ReleaseUnusedReservation(reservation));
        try
        {
            Assert.Same(context, child.TryGetContext());
        }
        finally
        {
            blocker.ContinueWrite.Set();
        }

        var triggerCompleted = triggerThread.Join(WriteProtocolAcceptance.RendezvousTimeout);
        WaitFor(childDetached, "the asynchronous same-context deferred sweep");
        await AsyncTestHelpers.WaitUntilAsync(
            () => child.TryGetContext() is null,
            timeout: WriteProtocolAcceptance.RendezvousTimeout,
            message: "the same-context deferred sweep did not finalize the orphan");

        // Assert
        Assert.True(triggerCompleted, "the callback-triggering attach did not complete");
        Assert.Null(triggerException);
        Assert.Null(completionException);
        Assert.Equal(0, Volatile.Read(ref nestedCallbackCount));
        Assert.False(graph.IsOwned(child));
    }

    [Fact]
    [Trait("Category", "Concurrency")]
    public async Task WhenForeignContextCallbackCompletesScalarWriteDuringDeferredSweep_ThenOriginContextEventuallySweeps()
    {
        // Arrange
        using var blocker = new BlockingScalarWriteInterceptor();
        using var childDetached = new ManualResetEventSlim(false);
        var firstContext = CreateContext()
            .WithService(() => blocker, _ => false);
        var firstGraph = GetGraph(firstContext);
        var firstRoot = new Person(firstContext) { FirstName = "root" };
        var child = new Person { FirstName = "child" };
        firstRoot.Father = child;
        var reservation = firstGraph.ReserveForStructuralWrite(
            (InterceptorExecutor)((IInterceptorSubject)child).Executor);
        firstRoot.Father = null;
        firstContext.TryGetLifecycleInterceptor()!.SubjectDetaching += change =>
        {
            if (change.IsContextDetach && ReferenceEquals(change.Subject, child))
            {
                childDetached.Set();
            }
        };

        Person? foreignTrigger = null;
        var foreignHandler = new DelegateLifecycleHandler(change =>
        {
            if (change.IsContextAttach && ReferenceEquals(change.Subject, foreignTrigger))
            {
                child.FirstName = "updated";
            }
        });
        var foreignContext = CreateContext()
            .WithService(() => foreignHandler, _ => false);
        foreignTrigger = new Person { FirstName = "trigger" };
        blocker.Arm(child, nameof(Person.FirstName));
        Exception? triggerException = null;
        var triggerThread = new Thread(
            () => triggerException = Record.Exception(() => foreignTrigger.AttachToContext(foreignContext)))
        {
            IsBackground = true
        };

        // Act
        triggerThread.Start();
        WaitFor(blocker.WriteEntered, "the foreign-context callback scalar write");
        var completionException = Record.Exception(() => firstGraph.ReleaseUnusedReservation(reservation));
        try
        {
            Assert.Same(firstContext, child.TryGetContext());
        }
        finally
        {
            blocker.ContinueWrite.Set();
        }

        var triggerCompleted = triggerThread.Join(WriteProtocolAcceptance.RendezvousTimeout);
        WaitFor(childDetached, "the asynchronous foreign-context deferred sweep");
        await AsyncTestHelpers.WaitUntilAsync(
            () => child.TryGetContext() is null,
            timeout: WriteProtocolAcceptance.RendezvousTimeout,
            message: "the foreign-context deferred sweep did not finalize the orphan");

        // Assert
        Assert.True(triggerCompleted, "the foreign callback-triggering attach did not complete");
        Assert.Null(triggerException);
        Assert.Null(completionException);
        Assert.False(firstGraph.IsOwned(child));
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

}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TemporaryRootTraceCollection
{
    public const string Name = "Temporary root trace";
}
