using System.Collections.Concurrent;
using System.Diagnostics;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Testing;

namespace Namotion.Interceptor.Tests.Interceptors;

public class StructuralWriteLeaseTests
{
    private sealed class ThrowingCompletionCoordinator : ITopologyAdmissionCoordinator
    {
        internal int LeaseAdmissions { get; private set; }

        public StructuralWriteLease AcquireStructuralWriteLease(InterceptorExecutor executor)
        {
            LeaseAdmissions++;
            throw new NotSupportedException();
        }

        public Exception? CompleteStructuralWrite(
            InterceptorExecutor executor,
            StructuralWriteLease lease,
            Exception? primaryException) =>
            throw new InvalidOperationException("completion failed");

        public OwnershipReservationToken AcquireOwnershipReservation(
            InterceptorExecutor executor,
            ReservationMode mode,
            bool joinExclusive) =>
            throw new NotSupportedException();

        public void CompleteOwnershipReservation(
            InterceptorExecutor executor,
            OwnershipReservationToken token,
            bool retainCommittedOwnership) =>
            throw new NotSupportedException();
    }

    private sealed class ThrowingTraceListener : TraceListener
    {
        private readonly int _threadId = Environment.CurrentManagedThreadId;

        public override void Write(string? message) => ThrowOnTestThread();

        public override void WriteLine(string? message) => ThrowOnTestThread();

        private void ThrowOnTestThread()
        {
            if (Environment.CurrentManagedThreadId == _threadId)
            {
                throw new InvalidOperationException("trace failed");
            }
        }
    }

    private sealed class CountingWriteInterceptor : IWriteInterceptor
    {
        internal int Entries { get; private set; }

        public void WriteProperty<TProperty>(
            ref PropertyWriteContext<TProperty> context,
            WriteInterceptionDelegate<TProperty> next)
        {
            Entries++;
            next(ref context);
        }
    }

    private sealed class RouteChangingMetadataSubject : IInterceptorSubject
    {
        private static readonly IReadOnlyDictionary<string, SubjectPropertyMetadata> Metadata =
            new Dictionary<string, SubjectPropertyMetadata>
            {
                [nameof(Child)] = new(
                    nameof(Child),
                    typeof(StructuralHolder),
                    [],
                    getValue: null,
                    static (subject, value) => ((RouteChangingMetadataSubject)subject).Child = (StructuralHolder?)value,
                    isIntercepted: true,
                    isDynamic: false)
            };

        private IInterceptorExecutor? _executor;
        private Action? _onNextPropertiesRead;

        internal StructuralHolder? Child { get; set; }

        internal Action? OnNextPropertiesRead
        {
            set => _onNextPropertiesRead = value;
        }

        public IInterceptorExecutor Executor => InterceptorExecutor.GetOrCreate(ref _executor, this);

        public ConcurrentDictionary<(string? property, string key), object?> Data { get; } = new();

        public IReadOnlyDictionary<string, SubjectPropertyMetadata> Properties
        {
            get
            {
                Interlocked.Exchange(ref _onNextPropertiesRead, null)?.Invoke();
                return Metadata;
            }
        }

        public void AddProperties(params IEnumerable<SubjectPropertyMetadata> properties) =>
            throw new NotSupportedException();
    }

    private sealed class VetoingWriteInterceptor : IWriteInterceptor
    {
        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
        }
    }

    private sealed class ThrowingWriteInterceptor : IWriteInterceptor
    {
        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            throw new InvalidOperationException("write failed");
        }
    }

    private sealed class ReentrantWriteInterceptor : IWriteInterceptor
    {
        private bool _isReentrantWrite;

        internal required InterceptorExecutor Executor { get; init; }

        internal int MaximumLeaseCount { get; private set; }

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            MaximumLeaseCount = Math.Max(MaximumLeaseCount, Executor.StructuralLeaseCount);
            if (!_isReentrantWrite)
            {
                _isReentrantWrite = true;
                try
                {
                    Executor.SetPropertyValue(
                        "NestedChild",
                        new StructuralHolder(),
                        (StructuralHolder?)null,
                        static (_, _) => { });
                }
                finally
                {
                    _isReentrantWrite = false;
                }
            }

            next(ref context);
        }
    }

    private sealed class CountingReadInterceptor : IReadInterceptor
    {
        internal int ReadCount { get; private set; }

        public TProperty ReadProperty<TProperty>(ref PropertyReadContext<TProperty> context, ReadInterceptionDelegate<TProperty> next)
        {
            ReadCount++;
            return next(ref context);
        }
    }

    private static InterceptorExecutor CreateExecutor()
    {
        return (InterceptorExecutor)((IInterceptorSubject)new StructuralHolder()).Executor;
    }

    [Fact]
    public void WhenTwoStructuralLeasesAreAcquired_ThenTheyShareTheStableAttachmentAndReleaseIndependently()
    {
        // Arrange
        var executor = CreateExecutor();

        // Act
        var first = executor.TryAcquireStructuralWriteLease();
        var second = executor.TryAcquireStructuralWriteLease();

        // Assert
        Assert.NotSame(first, second);
        Assert.Equal(2, executor.StructuralLeaseCount);
        Assert.Null(first.Context);
        Assert.Null(second.Context);
        Assert.Equal(0, first.AttachmentRevision);
        Assert.Equal(0, second.AttachmentRevision);

        first.Dispose();
        first.Dispose();
        Assert.Equal(1, executor.StructuralLeaseCount);

        second.Dispose();
        Assert.Equal(0, executor.StructuralLeaseCount);
    }

    [Fact]
    public void WhenLeaseCompletionAndTracingFail_ThenDisposeRemainsNoThrow()
    {
        // Arrange
        var executor = CreateExecutor();
        var lease = new StructuralWriteLease(executor, null, 0, new ThrowingCompletionCoordinator());
        var listener = new ThrowingTraceListener();
        Trace.Listeners.Add(listener);

        try
        {
            // Act
            var exception = Record.Exception(lease.Dispose);

            // Assert
            Assert.Null(exception);
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }
    }

    [Fact]
    public void WhenExpectedDetachedRouteHasAlreadyAttached_ThenExactLeaseAdmissionRejectsTheRoute()
    {
        // Arrange
        var executor = CreateExecutor();
        var context = InterceptorSubjectContext.Create();
        Assert.True(executor.TryUpdateAttachment(
            executor.AttachmentRevision,
            context,
            SubjectAttachmentAnchorKind.Explicit,
            out _));

        // Act
        var exception = Record.Exception(() => executor.TryAcquireStructuralWriteLease(expectedContext: null));

        // Assert
        Assert.IsType<AttachmentRouteChangedException>(exception);
        Assert.Equal(0, executor.StructuralLeaseCount);
    }

    [Fact]
    public void WhenAttachedRouteDetachesDuringMissingReaderValidation_ThenWriteRetriesOnDetachedRoute()
    {
        // Arrange
        var coordinator = new ThrowingCompletionCoordinator();
        var interceptor = new CountingWriteInterceptor();
        var context = InterceptorSubjectContext.Create();
        context.AddService(coordinator);
        context.AddService(interceptor);
        var subject = new RouteChangingMetadataSubject();
        var executor = (InterceptorExecutor)subject.Executor;
        Assert.True(executor.TryUpdateAttachment(
            executor.AttachmentRevision,
            context,
            SubjectAttachmentAnchorKind.Explicit,
            out _));
        subject.OnNextPropertiesRead = () => Assert.True(executor.TryUpdateAttachment(
            executor.AttachmentRevision,
            null,
            SubjectAttachmentAnchorKind.None,
            out _));
        var child = new StructuralHolder();
        var written = false;

        // Act
        var exception = Record.Exception(() => written = executor.SetPropertyValue(
            nameof(RouteChangingMetadataSubject.Child),
            child,
            (StructuralHolder?)null,
            (_, value) => subject.Child = value));

        // Assert
        Assert.Null(exception);
        Assert.True(written);
        Assert.Same(child, subject.Child);
        Assert.Null(executor.AttachedContext);
        Assert.Equal(0, coordinator.LeaseAdmissions);
        Assert.Equal(0, interceptor.Entries);
    }

    [Fact]
    public void WhenStableAttachedStructuralRouteHasNoRawReader_ThenItRejectsBeforeAdmissionOrExecution()
    {
        // Arrange
        var coordinator = new ThrowingCompletionCoordinator();
        var interceptor = new CountingWriteInterceptor();
        var context = InterceptorSubjectContext.Create();
        context.AddService(coordinator);
        context.AddService(interceptor);
        var subject = new RouteChangingMetadataSubject();
        var executor = (InterceptorExecutor)subject.Executor;
        Assert.True(executor.TryUpdateAttachment(
            executor.AttachmentRevision,
            context,
            SubjectAttachmentAnchorKind.Explicit,
            out _));
        var writerExecuted = false;

        // Act
        var exception = Record.Exception(() => executor.SetPropertyValue(
            nameof(RouteChangingMetadataSubject.Child),
            new StructuralHolder(),
            (StructuralHolder?)null,
            (_, _) => writerExecuted = true));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
        Assert.False(writerExecuted);
        Assert.Same(context, executor.AttachedContext);
        Assert.Equal(0, coordinator.LeaseAdmissions);
        Assert.Equal(0, interceptor.Entries);
        Assert.Equal(0, executor.StructuralLeaseCount);
    }

    [Fact]
    public void WhenAttachmentTransitionIsAttemptedDuringAStructuralLease_ThenItFailsWithoutChangingTheAttachment()
    {
        // Arrange
        var executor = CreateExecutor();
        var context = InterceptorSubjectContext.Create();
        using var lease = executor.TryAcquireStructuralWriteLease();

        // Act & Assert
        Assert.Throws<LifecycleConflictException>(() =>
            executor.TryUpdateAttachment(0, context, SubjectAttachmentAnchorKind.Explicit, out _));
        Assert.Null(executor.AttachedContext);
        Assert.Equal(SubjectAttachmentAnchorKind.None, executor.AttachmentAnchor);
        Assert.Equal(0, executor.AttachmentRevision);
        Assert.Equal(AttachmentPhase.Stable, executor.CurrentAttachmentPhase);
    }

    [Fact]
    public void WhenStructuralLeaseIsAttemptedDuringAnExclusiveTransition_ThenItFailsPromptly()
    {
        // Arrange
        var executor = CreateExecutor();
        using var transition = Assert.IsType<InterceptorExecutor.AttachmentTransition>(
            executor.TryAcquireAttachmentTransition(0, AttachmentPhase.Attaching, out var currentRevision));

        // Act & Assert
        Assert.Equal(0, currentRevision);
        Assert.Throws<LifecycleConflictException>(() => executor.TryAcquireStructuralWriteLease());
        Assert.Equal(AttachmentPhase.Attaching, executor.CurrentAttachmentPhase);
        Assert.Equal(0, executor.StructuralLeaseCount);
    }

    [Fact]
    public void WhenExclusiveTransitionThrows_ThenItsPhaseIsReleased()
    {
        // Arrange
        var executor = CreateExecutor();
        var firstContext = InterceptorSubjectContext.Create();
        var secondContext = InterceptorSubjectContext.Create();
        Assert.True(executor.TryUpdateAttachment(0, firstContext, SubjectAttachmentAnchorKind.Explicit, out var revision));

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() =>
            executor.TryUpdateAttachment(revision, secondContext, SubjectAttachmentAnchorKind.Explicit, out _));
        Assert.Equal(AttachmentPhase.Stable, executor.CurrentAttachmentPhase);

        using var lease = executor.TryAcquireStructuralWriteLease();
        Assert.Same(firstContext, lease.Context);
        Assert.Equal(revision, lease.AttachmentRevision);
    }

    [Fact]
    public void WhenStructuralWriteIsVetoed_ThenItsLeaseIsReleased()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        context.AddService(new VetoingWriteInterceptor());
        var subject = new StructuralHolder(context);
        var executor = (InterceptorExecutor)((IInterceptorSubject)subject).Executor;

        // Act
        var written = executor.SetPropertyValue(
            nameof(StructuralHolder.Child),
            new StructuralHolder(),
            (StructuralHolder?)null,
            static (_, _) => { });

        // Assert
        Assert.False(written);
        Assert.Equal(0, executor.StructuralLeaseCount);
    }

    [Fact]
    public void WhenStructuralWriteThrows_ThenItsLeaseIsReleased()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        context.AddService(new ThrowingWriteInterceptor());
        var subject = new StructuralHolder(context);
        var executor = (InterceptorExecutor)((IInterceptorSubject)subject).Executor;

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => executor.SetPropertyValue(
            nameof(StructuralHolder.Child),
            new StructuralHolder(),
            (StructuralHolder?)null,
            static (_, _) => { }));
        Assert.Equal(0, executor.StructuralLeaseCount);
    }

    [Fact]
    public void WhenStructuralWriteReentersOnTheSameSubject_ThenBothWritesHoldIndependentLeasesAndComplete()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var subject = new StructuralHolder(context);
        var executor = (InterceptorExecutor)((IInterceptorSubject)subject).Executor;
        var interceptor = new ReentrantWriteInterceptor { Executor = executor };
        context.AddService(interceptor);
        var backingWritten = false;

        // Act
        var written = executor.SetPropertyValue(
            nameof(StructuralHolder.Child),
            new StructuralHolder(),
            (StructuralHolder?)null,
            (_, _) => backingWritten = true);

        // Assert
        Assert.True(written);
        Assert.True(backingWritten);
        Assert.Equal(2, interceptor.MaximumLeaseCount);
        Assert.Equal(0, executor.StructuralLeaseCount);
        Assert.Equal(2, executor.Revision);
    }

    [Fact]
    public void WhenLeasePinsAnAttachedState_ThenConflictingDetachCannotChangeItsContextOrRevision()
    {
        // Arrange
        var executor = CreateExecutor();
        var context = InterceptorSubjectContext.Create();
        Assert.True(executor.TryUpdateAttachment(0, context, SubjectAttachmentAnchorKind.Explicit, out var attachmentRevision));
        using var lease = executor.TryAcquireStructuralWriteLease();

        // Act & Assert
        Assert.Throws<LifecycleConflictException>(() => executor.TryUpdateAttachment(
            attachmentRevision,
            null,
            SubjectAttachmentAnchorKind.None,
            out _));
        Assert.Same(context, lease.Context);
        Assert.Equal(attachmentRevision, lease.AttachmentRevision);
        Assert.Same(context, executor.AttachedContext);
        Assert.Equal(SubjectAttachmentAnchorKind.Explicit, executor.AttachmentAnchor);
        Assert.Equal(attachmentRevision, executor.AttachmentRevision);
    }

    [Fact]
    public void WhenGeneratedStructuralSetterRuns_ThenItsTrustedReaderAndStoreRunUnderTheTerminalLock()
    {
        // Arrange
        var executor = CreateExecutor();
        var readerHeldTerminal = false;
        var storeHeldTerminal = false;

        // Act
        var written = executor.SetGeneratedPropertyValue<StructuralHolder?>(
            nameof(StructuralHolder.Child),
            new StructuralHolder(),
            _ =>
            {
                readerHeldTerminal = Monitor.IsEntered(executor.SyncRoot);
                return (StructuralHolder?)null;
            },
            (_, _) => storeHeldTerminal = Monitor.IsEntered(executor.SyncRoot));

        // Assert
        Assert.True(written);
        Assert.True(readerHeldTerminal);
        Assert.True(storeHeldTerminal);
        Assert.Equal(1, executor.Revision);
    }

    [Fact]
    public async Task WhenGeneratedStructuralGetterReachesItsRawReader_ThenItOwnsTheTerminalLock()
    {
        // Arrange
        var executor = CreateExecutor();
        var terminalHeld = new ManualResetEventSlim(false);
        var releaseTerminal = new ManualResetEventSlim(false);
        var readerReached = new ManualResetEventSlim(false);
        var readerHeldTerminal = false;
        var readCompleted = false;
        var terminalHolder = new Thread(() =>
        {
            lock (executor.SyncRoot)
            {
                terminalHeld.Set();
                releaseTerminal.Wait();
            }
        }) { IsBackground = true };
        var reader = new Thread(() =>
        {
            executor.GetGeneratedPropertyValue<StructuralHolder?>(
                nameof(StructuralHolder.Child),
                _ =>
                {
                    readerHeldTerminal = Monitor.IsEntered(executor.SyncRoot);
                    readerReached.Set();
                    return (StructuralHolder?)null;
                });
            readCompleted = true;
        }) { IsBackground = true };

        // Act
        terminalHolder.Start();
        await AsyncTestHelpers.WaitUntilAsync(() => terminalHeld.IsSet);
        reader.Start();
        try
        {
            await AsyncTestHelpers.WaitUntilAsync(
                () => (reader.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0,
                message: "the structural reader did not wait for the terminal");
            Assert.False(readerReached.IsSet);
            Assert.False(readCompleted);
        }
        finally
        {
            releaseTerminal.Set();
        }

        await AsyncTestHelpers.WaitUntilAsync(
            () => !terminalHolder.IsAlive && !reader.IsAlive,
            message: "the structural reader did not complete");

        // Assert
        Assert.True(readerReached.IsSet);
        Assert.True(readerHeldTerminal);
        Assert.True(readCompleted);
    }

    [Fact]
    public void WhenGeneratedStructuralSetterReadsItsChangedValue_ThenTheRawReadIsLockedWithoutRunningReadInterceptors()
    {
        // Arrange
        var readInterceptor = new CountingReadInterceptor();
        var context = InterceptorSubjectContext.Create();
        context.AddService(readInterceptor);
        var subject = new StructuralHolder(context);
        var executor = (InterceptorExecutor)((IInterceptorSubject)subject).Executor;
        var readerHeldTerminal = false;

        // Act
        var value = executor.GetGeneratedPropertyValue<StructuralHolder?>(
            nameof(StructuralHolder.Child),
            _ =>
            {
                readerHeldTerminal = Monitor.IsEntered(executor.SyncRoot);
                return null;
            },
            executeInterceptors: false);

        // Assert
        Assert.Null(value);
        Assert.True(readerHeldTerminal);
        Assert.Equal(0, readInterceptor.ReadCount);
    }
}
