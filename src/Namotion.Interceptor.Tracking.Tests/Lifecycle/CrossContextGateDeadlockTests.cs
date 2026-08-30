using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>Verifies that one logical context is enforced before any foreign topology admission.</summary>
public class CrossContextGateDeadlockTests
{
    private static IInterceptorSubjectContext CreateContext()
    {
        return InterceptorSubjectContext
            .Create()
            .WithLifecycle();
    }

    [Fact]
    public void WhenDownstreamWriteTargetsSecondContext_ThenItIsRejectedBeforeForeignAdmission()
    {
        // Arrange: any foreign coordinator, gate, lease or chain entry precedes logical-context rejection.
        var foreignLifecycle = new AdmissionProbeLifecycleInterceptor();
        var foreignContext = InterceptorSubjectContext.Create();
        foreignContext.AddService(foreignLifecycle);
        var foreignTarget = new Person();
        foreignTarget.AttachToContext(foreignContext);

        var attempt = new CrossContextAttemptInterceptor();
        var context = CreateContext();
        context.AddService<IWriteInterceptor>(attempt);
        var parent = new Person(context);
        attempt.Arm(parent, nameof(Person.Mother), () => foreignTarget.Father = new Person());

        // Act
        parent.Mother = new Person();

        // Assert
        Assert.IsAssignableFrom<InvalidOperationException>(attempt.Exception);
        Assert.Equal(0, foreignLifecycle.CoordinatorEntries);
        Assert.Equal(0, foreignLifecycle.GateEntries);
        Assert.Equal(0, foreignLifecycle.LeaseAdmissions);
        Assert.Equal(0, foreignLifecycle.WriteChainEntries);
        Assert.Null(foreignTarget.Father);
    }

    /// <summary>
    /// Reproduces the finding that a cross-context structural write from a lifecycle callback takes
    /// the foreign gate before any guard runs. The rendezvous is artificial, because it lines both
    /// callbacks up inside their own gates at the same time; the acquisition order they then take
    /// is the production one. A deadlock shows up as a bounded join failing rather than as a hung
    /// suite, and the worker threads are background threads so a deadlocked pair cannot keep the
    /// run alive.
    /// </summary>
    [Fact]
    [Trait("Category", "Concurrency")]
    public void WhenTwoAttachCallbacksCrossWriteIntoEachOthersContexts_ThenBothOperationsComplete()
    {
        // Arrange: each context holds a subject the other context's attach callback writes to.
        var rendezvous = new Barrier(2);
        var rendezvousReached = 0;
        Person? pinnedInFirst = null;
        Person? pinnedInSecond = null;
        Exception? firstCrossWriteException = null;
        Exception? secondCrossWriteException = null;

        var firstHandler = new DelegateLifecycleHandler(change =>
        {
            if (!change.IsContextAttach || change.Subject is not Person { FirstName: "trigger" } || pinnedInSecond is null)
            {
                return;
            }

            if (!rendezvous.SignalAndWait(WriteProtocolAcceptance.RendezvousTimeout))
            {
                return;
            }

            Interlocked.Increment(ref rendezvousReached);
            firstCrossWriteException = Record.Exception(
                () => pinnedInSecond.Father = new Person { FirstName = "fromFirst" });
        });

        var secondHandler = new DelegateLifecycleHandler(change =>
        {
            if (!change.IsContextAttach || change.Subject is not Person { FirstName: "trigger" } || pinnedInFirst is null)
            {
                return;
            }

            if (!rendezvous.SignalAndWait(WriteProtocolAcceptance.RendezvousTimeout))
            {
                return;
            }

            Interlocked.Increment(ref rendezvousReached);
            secondCrossWriteException = Record.Exception(
                () => pinnedInFirst.Father = new Person { FirstName = "fromSecond" });
        });

        var firstContext = CreateContext().WithService(() => firstHandler, _ => false);
        var secondContext = CreateContext().WithService(() => secondHandler, _ => false);

        pinnedInFirst = new Person { FirstName = "pinned" };
        ((IInterceptorSubject)pinnedInFirst).AttachToContext(firstContext);
        pinnedInSecond = new Person { FirstName = "pinned" };
        ((IInterceptorSubject)pinnedInSecond).AttachToContext(secondContext);

        var firstTrigger = new Person { FirstName = "trigger" };
        var secondTrigger = new Person { FirstName = "trigger" };

        var firstAttach = new Thread(
            () => ((IInterceptorSubject)firstTrigger).AttachToContext(firstContext)) { IsBackground = true };
        var secondAttach = new Thread(
            () => ((IInterceptorSubject)secondTrigger).AttachToContext(secondContext)) { IsBackground = true };

        // Act
        firstAttach.Start();
        secondAttach.Start();
        var firstCompleted = firstAttach.Join(WriteProtocolAcceptance.JoinTimeout);
        var secondCompleted = firstCompleted && secondAttach.Join(WriteProtocolAcceptance.JoinTimeout);

        // Assert: both callbacks reached the cross write, so a timeout cannot pass by serializing.
        Assert.Equal(2, Volatile.Read(ref rendezvousReached));

        // Termination is necessary but not sufficient. The remedy is that the cross-context write is
        // rejected, so both assertions are made: completion first, because a deadlock reports better
        // as a timeout than as a null exception, and then the rejection itself. Asserting the
        // rejection is what stops this test passing if the deadlock disappears because the gate
        // stopped being held here rather than because the write became guarded.
        Assert.True(firstCompleted && secondCompleted,
            "probable ABBA deadlock on two lifecycle gates: the first attach " +
            $"{(firstCompleted ? "completed" : "did not complete")} and the second attach " +
            $"{(secondCompleted ? "completed" : "did not complete")} within {WriteProtocolAcceptance.JoinTimeout.TotalSeconds:F0} seconds");
        Assert.IsAssignableFrom<InvalidOperationException>(firstCrossWriteException);
        Assert.IsAssignableFrom<InvalidOperationException>(secondCrossWriteException);
    }

    /// <summary>
    /// A third-party write interceptor registered downstream of the lifecycle and retained inside
    /// the writing context's Core logical scope through the complete unwind.
    /// </summary>
    private sealed class CrossContextWriteInterceptor(Barrier rendezvous, TimeSpan rendezvousTimeout) : IWriteInterceptor
    {
        private volatile IInterceptorSubject? _armedSubject;
        private volatile string? _armedPropertyName;
        private Action? _crossContextWrite;

        public int RendezvousReached;

        public Exception? CrossContextWriteException;

        public void Arm(IInterceptorSubject subject, string propertyName, Action crossContextWrite)
        {
            _crossContextWrite = crossContextWrite;
            _armedPropertyName = propertyName;
            _armedSubject = subject;
        }

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            var isArmed = ReferenceEquals(context.Property.Subject, _armedSubject) &&
                          context.Property.Name == _armedPropertyName;

            next(ref context);

            if (!isArmed)
            {
                return;
            }

            _armedSubject = null;

            if (!rendezvous.SignalAndWait(rendezvousTimeout))
            {
                return;
            }

            Interlocked.Increment(ref RendezvousReached);
            CrossContextWriteException = Record.Exception(_crossContextWrite!);
        }
    }

    private sealed class CrossContextAttemptInterceptor : IWriteInterceptor
    {
        private IInterceptorSubject? _subject;
        private string? _propertyName;
        private Action? _write;

        public Exception? Exception { get; private set; }

        public void Arm(IInterceptorSubject subject, string propertyName, Action write)
        {
            _subject = subject;
            _propertyName = propertyName;
            _write = write;
        }

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            if (ReferenceEquals(context.Property.Subject, _subject) && context.Property.Name == _propertyName)
            {
                _subject = null;
                Exception = Record.Exception(_write!);
            }

            next(ref context);
        }
    }

    private sealed class AdmissionProbeLifecycleInterceptor : ILifecycleInterceptor, ITopologyAdmissionCoordinator
    {
        public int CoordinatorEntries { get; private set; }

        public int GateEntries { get; private set; }

        public int LeaseAdmissions { get; private set; }

        public int WriteChainEntries { get; private set; }

        StructuralWriteLease ITopologyAdmissionCoordinator.AcquireStructuralWriteLease(InterceptorExecutor executor)
        {
            CoordinatorEntries++;
            GateEntries++;
            LeaseAdmissions++;
            throw new InvalidOperationException("The foreign topology coordinator must not be entered.");
        }

        Exception? ITopologyAdmissionCoordinator.CompleteStructuralWrite(
            InterceptorExecutor executor,
            StructuralWriteLease lease,
            Exception? primaryException) =>
            throw new InvalidOperationException("No foreign structural lease should require completion.");

        OwnershipReservationToken ITopologyAdmissionCoordinator.AcquireOwnershipReservation(
            InterceptorExecutor executor,
            ReservationMode mode) =>
            throw new InvalidOperationException("The foreign topology coordinator must not reserve ownership.");

        void ITopologyAdmissionCoordinator.CompleteOwnershipReservation(
            InterceptorExecutor executor,
            OwnershipReservationToken token,
            bool retainCommittedOwnership) =>
            throw new InvalidOperationException("No foreign ownership reservation should require completion.");

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            WriteChainEntries++;
            next(ref context);
        }

        public void AttachSubjectToContext(
            IInterceptorSubject subject,
            IInterceptorSubjectContext context,
            SubjectAttachmentAnchorKind anchor)
        {
            subject.Executor.TryGetAttachment(out _, out _, out var revision);
            Assert.True(subject.Executor.TryUpdateAttachment(revision, context, anchor, out _));
        }

        public void DetachSubjectFromContext(IInterceptorSubject subject, IInterceptorSubjectContext context)
        {
            subject.Executor.TryGetAttachment(out _, out _, out var revision);
            Assert.True(subject.Executor.TryUpdateAttachment(
                revision, null, SubjectAttachmentAnchorKind.None, out _));
        }

        public bool TryAddProperties(SubjectPropertyRegistration registration)
        {
            registration.Publish();
            return true;
        }
    }

    /// <summary>
    /// Two ordinary downstream write interceptors cross-write into each other's contexts after their
    /// terminals return. Each still carries its Core logical-context scope through that unwind.
    ///
    /// The rendezvous is artificial, because it lines both interceptors up inside their own gates at
    /// the same time; the acquisition order they then take is the production one.
    ///
    /// Like the callback half above, this asserts the rejection and not merely termination.
    /// </summary>
    [Fact]
    [Trait("Category", "Concurrency")]
    public void WhenTwoDownstreamWriteInterceptorsCrossWriteIntoEachOthersContexts_ThenBothOperationsComplete()
    {
        // Arrange: each context holds a subject the other context's downstream interceptor writes to.
        var rendezvous = new Barrier(2);
        var firstInterceptor = new CrossContextWriteInterceptor(rendezvous, WriteProtocolAcceptance.RendezvousTimeout);
        var secondInterceptor = new CrossContextWriteInterceptor(rendezvous, WriteProtocolAcceptance.RendezvousTimeout);

        var firstContext = CreateContext();
        firstContext.AddService<IWriteInterceptor>(firstInterceptor);
        var secondContext = CreateContext();
        secondContext.AddService<IWriteInterceptor>(secondInterceptor);

        var pinnedInFirst = new Person { FirstName = "pinned" };
        ((IInterceptorSubject)pinnedInFirst).AttachToContext(firstContext);
        var pinnedInSecond = new Person { FirstName = "pinned" };
        ((IInterceptorSubject)pinnedInSecond).AttachToContext(secondContext);

        var firstTrigger = new Person { FirstName = "trigger" };
        ((IInterceptorSubject)firstTrigger).AttachToContext(firstContext);
        var secondTrigger = new Person { FirstName = "trigger" };
        ((IInterceptorSubject)secondTrigger).AttachToContext(secondContext);

        firstInterceptor.Arm(firstTrigger, nameof(Person.Mother),
            () => pinnedInSecond.Father = new Person { FirstName = "fromFirst" });
        secondInterceptor.Arm(secondTrigger, nameof(Person.Mother),
            () => pinnedInFirst.Father = new Person { FirstName = "fromSecond" });

        var firstWrite = new Thread(
            () => firstTrigger.Mother = new Person { FirstName = "m" }) { IsBackground = true };
        var secondWrite = new Thread(
            () => secondTrigger.Mother = new Person { FirstName = "m" }) { IsBackground = true };

        // Act
        firstWrite.Start();
        secondWrite.Start();
        var firstCompleted = firstWrite.Join(WriteProtocolAcceptance.JoinTimeout);
        var secondCompleted = firstCompleted && secondWrite.Join(WriteProtocolAcceptance.JoinTimeout);

        // Assert: both interceptors reached the cross write, so a timeout cannot pass by serializing.
        Assert.Equal(2,
            Volatile.Read(ref firstInterceptor.RendezvousReached) +
            Volatile.Read(ref secondInterceptor.RendezvousReached));

        Assert.True(firstCompleted && secondCompleted,
            "probable ABBA deadlock on two lifecycle gates: the first structural write " +
            $"{(firstCompleted ? "completed" : "did not complete")} and the second structural write " +
            $"{(secondCompleted ? "completed" : "did not complete")} within {WriteProtocolAcceptance.JoinTimeout.TotalSeconds:F0} seconds");

        Assert.IsAssignableFrom<InvalidOperationException>(firstInterceptor.CrossContextWriteException);
        Assert.IsAssignableFrom<InvalidOperationException>(secondInterceptor.CrossContextWriteException);
    }
}
