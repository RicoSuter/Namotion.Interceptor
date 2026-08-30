using System.Runtime.CompilerServices;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

[Collection(TerminalBoundaryCoordinatorCollection.Name)]
public class TerminalBoundaryCoordinatorTests
{
    private static readonly TimeSpan LockProbeTimeout = TimeSpan.FromSeconds(2);

    private static IInterceptorSubjectContext CreateContext(IWriteInterceptor? interceptor = null)
    {
        var context = InterceptorSubjectContext.Create().WithLifecycle();
        if (interceptor is not null)
        {
            context.AddService(interceptor);
        }

        return context;
    }

    [Fact]
    public void WhenDownstreamInterceptorReplacesStructuralValue_ThenTerminalPublishesReplacement()
    {
        // Arrange
        var interceptor = new RewritingInterceptor();
        var context = CreateContext(interceptor);
        var parent = new Person(context);
        var requested = new Person { FirstName = "requested" };
        var replacement = new Person { FirstName = "replacement" };
        interceptor.Arm(parent, nameof(Person.Father), replacement);

        // Act
        parent.Father = requested;

        // Assert
        Assert.Same(replacement, parent.Father);
        Assert.Same(context, replacement.TryGetContext());
        Assert.Equal(1, replacement.GetReferenceCount());
        Assert.Null(requested.TryGetContext());
    }

    [Fact]
    public void WhenInitiallyForeignValueIsRewrittenToLocal_ThenOnlyFinalValueIsValidated()
    {
        // Arrange
        var interceptor = new RewritingInterceptor();
        var context = CreateContext(interceptor);
        var otherContext = CreateContext();
        var parent = new Person(context);
        var foreign = new Person(otherContext) { FirstName = "foreign" };
        var replacement = new Person { FirstName = "replacement" };
        interceptor.Arm(parent, nameof(Person.Father), replacement);

        // Act
        parent.Father = foreign;

        // Assert
        Assert.Same(replacement, parent.Father);
        Assert.Same(context, replacement.TryGetContext());
        Assert.Same(otherContext, foreign.TryGetContext());
    }

    [Fact]
    public void WhenInitiallyLocalValueIsRewrittenToForeign_ThenWriteIsRejectedBeforeStore()
    {
        // Arrange
        var interceptor = new RewritingInterceptor();
        var context = CreateContext(interceptor);
        var otherContext = CreateContext();
        var parent = new Person(context);
        var requested = new Person { FirstName = "requested" };
        var foreign = new Person(otherContext) { FirstName = "foreign" };
        interceptor.Arm(parent, nameof(Person.Father), foreign);

        // Act
        var exception = Record.Exception(() => parent.Father = requested);

        // Assert
        Assert.NotNull(exception);
        Assert.Null(parent.Father);
        Assert.Null(requested.TryGetContext());
        Assert.Same(otherContext, foreign.TryGetContext());
    }

    [Fact]
    public void WhenDownstreamRewriteIsVetoed_ThenNothingCommits()
    {
        // Arrange
        var interceptor = new RewritingInterceptor { Veto = true };
        var context = CreateContext(interceptor);
        var parent = new Person(context);
        var replacement = new Person { FirstName = "replacement" };
        interceptor.Arm(parent, nameof(Person.Father), replacement);
        var property = new PropertyReference(parent, nameof(Person.Father));
        property.TryGetWriteState(includeSourceCommitsInRevision: true, out var revision, out _);

        // Act
        parent.Father = new Person { FirstName = "requested" };

        // Assert
        Assert.Null(parent.Father);
        Assert.Null(replacement.TryGetContext());
        property.TryGetWriteState(includeSourceCommitsInRevision: true, out var revisionAfterWrite, out _);
        Assert.Equal(revision, revisionAfterWrite);
    }

    [Fact]
    [Trait("Category", "Concurrency")]
    public void WhenDownstreamInterceptorWaitsForSameContextWorker_ThenWorkerCompletesBeforeTerminal()
    {
        // Arrange
        var workerTarget = new Person();
        var interceptor = new WorkerWriteInterceptor();
        var context = CreateContext(interceptor);
        var parent = new Person(context);
        workerTarget.AttachToContext(context);
        interceptor.Arm(parent, nameof(Person.Mother), () => workerTarget.Father = new Person());

        // Act
        parent.Mother = new Person();

        // Assert
        Assert.True(interceptor.WorkerCompleted,
            "the downstream interceptor held the whole-chain topology gate while waiting for a worker that needed it");
        Assert.Null(interceptor.WorkerException);
    }

    [Fact]
    public void WhenDownstreamInterceptorThrowsAfterTerminal_ThenCommittedJournalStillDrains()
    {
        // Arrange
        var interceptor = new ThrowAfterNextInterceptor();
        var context = CreateContext(interceptor);
        var parent = new Person(context);
        var child = new Person();
        var attached = 0;
        context.TryGetLifecycleInterceptor()!.SubjectAttached += change =>
        {
            if (ReferenceEquals(change.Subject, child))
            {
                attached++;
            }
        };
        interceptor.Arm(parent, nameof(Person.Father));

        // Act
        var exception = Record.Exception(() => parent.Father = child);

        // Assert
        Assert.IsType<PostTerminalException>(exception);
        Assert.Same(child, parent.Father);
        Assert.Same(context, child.TryGetContext());
        Assert.Equal(1, child.GetReferenceCount());
        Assert.Equal(1, attached);
    }

    [Fact]
    public void WhenTopologyPreparationFails_ThenRawStorageAndCommittedGraphRemainUnchanged()
    {
        // Arrange
        var context = CreateContext();
        var parent = new Person(context);
        var child = new Person();
        parent.Father = child;
        var executor = (InterceptorExecutor)((IInterceptorSubject)child).Executor;
        using var transition = Assert.IsType<InterceptorExecutor.AttachmentTransition>(
            executor.TryAcquireAttachmentTransition(
                executor.AttachmentRevision,
                AttachmentPhase.Detaching,
                out _));

        // Act
        var exception = Record.Exception(() => parent.Father = null);

        // Assert
        Assert.IsType<LifecycleConflictException>(exception);
        Assert.Same(child, parent.Father);
        Assert.Same(context, child.TryGetContext());
        Assert.Equal(1, child.GetReferenceCount());
    }

    [Fact]
    public void WhenTerminalReturnsToDownstreamInterceptor_ThenParentLeaseRemainsUntilFullUnwind()
    {
        // Arrange
        var interceptor = new LeaseProbeInterceptor();
        var context = CreateContext(interceptor);
        var parent = new Person(context);
        interceptor.Arm(parent, nameof(Person.Father));

        // Act
        parent.Father = new Person();

        // Assert
        Assert.IsType<LifecycleConflictException>(interceptor.DetachAttemptException);
        Assert.Same(context, parent.TryGetContext());
    }

    [Fact]
    [Trait("Category", "Concurrency")]
    public void WhenConcurrentGeneratedWritersAreReleasedInReverseOrder_ThenEachTerminalPublishesInLockOrder()
    {
        // Arrange
        var first = new Person { FirstName = "first" };
        var second = new Person { FirstName = "second" };
        var interceptor = new OrderedTerminalInterceptor(first, second);
        var context = CreateContext(interceptor);
        var parent = new Person(context);
        Exception? firstException = null;
        Exception? secondException = null;
        var firstWriter = new Thread(() => firstException = Record.Exception(() => parent.Father = first)) { IsBackground = true };
        var secondWriter = new Thread(() => secondException = Record.Exception(() => parent.Father = second)) { IsBackground = true };

        // Act
        firstWriter.Start();
        Assert.True(interceptor.FirstArrived.Wait(WriteProtocolAcceptance.RendezvousTimeout));
        secondWriter.Start();
        var bothReachedTheChain = interceptor.SecondArrived.Wait(WriteProtocolAcceptance.RendezvousTimeout);
        interceptor.ReleaseSecond.Set();
        var secondCompleted = secondWriter.Join(WriteProtocolAcceptance.JoinTimeout);
        interceptor.ReleaseFirst.Set();
        var firstCompleted = firstWriter.Join(WriteProtocolAcceptance.JoinTimeout);

        // Assert
        Assert.True(bothReachedTheChain,
            "the first writer held a topology gate around its interceptor chain, so the second writer never reached the terminal rendezvous");
        Assert.True(firstCompleted && secondCompleted);
        Assert.Null(firstException);
        Assert.Null(secondException);
        Assert.Same(first, parent.Father);
        Assert.Same(context, first.TryGetContext());
        Assert.Null(second.TryGetContext());
    }

    [Fact]
    [Trait("Category", "Concurrency")]
    public void WhenOriginAndTimestampResolutionWaitForTerminalReader_ThenBothResolveBeforeTerminalLock()
    {
        // Arrange
        var context = CreateContext();
        var parent = new Person(context);
        var workers = new List<Thread>();
        var originReaderCompleted = false;
        var timestampReaderCompleted = false;

        bool RunReader()
        {
            var worker = new Thread(() => _ = parent.Father) { IsBackground = true };
            workers.Add(worker);
            worker.Start();
            return worker.Join(LockProbeTimeout);
        }

        var child = new BlockingEqualsPerson(() => originReaderCompleted = RunReader());
        var originalTimestampProvider = SubjectChangeContext.GetTimestampFunction;
        SubjectChangeContext.GetTimestampFunction = () =>
        {
            timestampReaderCompleted = RunReader();
            return new DateTimeOffset(638712864000000000, TimeSpan.Zero);
        };

        var property = new PropertyReference(parent, nameof(Person.Father));
        try
        {
            // Act
            property.SetValueFromOrigin(
                ChangeOrigin.FromSource(new object()),
                changedTimestamp: null,
                receivedTimestamp: null,
                value: child,
                sentValue: child);
        }
        finally
        {
            SubjectChangeContext.GetTimestampFunction = originalTimestampProvider;
            foreach (var worker in workers)
            {
                worker.Join(WriteProtocolAcceptance.JoinTimeout);
            }
        }

        // Assert
        Assert.True(originReaderCompleted, "origin equality ran while the terminal lock was held");
        Assert.True(timestampReaderCompleted, "the timestamp provider ran while the terminal lock was held");
    }

    private sealed class RewritingInterceptor : IWriteInterceptor
    {
        private IInterceptorSubject? _subject;
        private string? _propertyName;
        private object? _replacement;

        public bool Veto { get; init; }

        public void Arm(IInterceptorSubject subject, string propertyName, object? replacement)
        {
            _subject = subject;
            _propertyName = propertyName;
            _replacement = replacement;
        }

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            if (ReferenceEquals(context.Property.Subject, _subject) && context.Property.Name == _propertyName)
            {
                context.NewValue = (TProperty)_replacement!;
                if (Veto)
                {
                    return;
                }
            }

            next(ref context);
        }
    }

    private sealed class WorkerWriteInterceptor : IWriteInterceptor
    {
        private IInterceptorSubject? _subject;
        private string? _propertyName;
        private Action? _workerWrite;

        public bool WorkerCompleted { get; private set; }

        public Exception? WorkerException { get; private set; }

        public void Arm(IInterceptorSubject subject, string propertyName, Action workerWrite)
        {
            _subject = subject;
            _propertyName = propertyName;
            _workerWrite = workerWrite;
        }

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            if (ReferenceEquals(context.Property.Subject, _subject) && context.Property.Name == _propertyName)
            {
                _subject = null;
                var worker = new Thread(() => WorkerException = Record.Exception(_workerWrite!)) { IsBackground = true };
                worker.Start();
                WorkerCompleted = worker.Join(WriteProtocolAcceptance.JoinTimeout);
            }

            next(ref context);
        }
    }

    private sealed class ThrowAfterNextInterceptor : IWriteInterceptor
    {
        private IInterceptorSubject? _subject;
        private string? _propertyName;

        public void Arm(IInterceptorSubject subject, string propertyName)
        {
            _subject = subject;
            _propertyName = propertyName;
        }

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            var armed = ReferenceEquals(context.Property.Subject, _subject) && context.Property.Name == _propertyName;
            next(ref context);
            if (armed)
            {
                throw new PostTerminalException();
            }
        }
    }

    private sealed class LeaseProbeInterceptor : IWriteInterceptor
    {
        private IInterceptorSubject? _subject;
        private string? _propertyName;

        public Exception? DetachAttemptException { get; private set; }

        public void Arm(IInterceptorSubject subject, string propertyName)
        {
            _subject = subject;
            _propertyName = propertyName;
        }

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            var armed = ReferenceEquals(context.Property.Subject, _subject) && context.Property.Name == _propertyName;
            next(ref context);
            if (armed)
            {
                var executor = context.Property.Subject.Executor;
                executor.TryGetAttachment(out _, out _, out var revision);
                DetachAttemptException = Record.Exception(() => executor.TryUpdateAttachment(
                    revision, null, SubjectAttachmentAnchorKind.None, out _));
            }
        }
    }

    private sealed class OrderedTerminalInterceptor(Person first, Person second) : IWriteInterceptor
    {
        public ManualResetEventSlim FirstArrived { get; } = new(false);
        public ManualResetEventSlim SecondArrived { get; } = new(false);
        public ManualResetEventSlim ReleaseFirst { get; } = new(false);
        public ManualResetEventSlim ReleaseSecond { get; } = new(false);

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            if (context.NewValue is Person person && context.Property.Name == nameof(Person.Father))
            {
                if (ReferenceEquals(person, first))
                {
                    FirstArrived.Set();
                    ReleaseFirst.Wait(WriteProtocolAcceptance.RendezvousTimeout);
                }
                else if (ReferenceEquals(person, second))
                {
                    SecondArrived.Set();
                    ReleaseSecond.Wait(WriteProtocolAcceptance.RendezvousTimeout);
                }
            }

            next(ref context);
        }
    }

    private sealed class BlockingEqualsPerson(Func<bool> waitForReader) : Person
    {
        public override bool Equals(object? obj)
        {
            waitForReader();
            return ReferenceEquals(this, obj);
        }

        public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);
    }

    private sealed class PostTerminalException : Exception;
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TerminalBoundaryCoordinatorCollection
{
    public const string Name = nameof(TerminalBoundaryCoordinatorCollection);
}
