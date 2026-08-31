using System.Collections.Concurrent;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// A component snapshot is read before reservations and topology-gate preparation. These tests put
/// a deterministic mutation after one participant's scan and before preparation, so publication
/// must validate that participant rather than trusting a locally stable but subsequently stale
/// snapshot. The last case pins participant-scoped validation: unrelated graph publication is not
/// a reason to repeat user capture.
/// </summary>
public class CaptureValidationTests
{
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
    [Trait("Category", "Concurrency")]
    public void WhenDescendantOutgoingEdgeChangesAfterCapture_ThenTerminalRecapturesWithoutReplayingInterceptors()
    {
        // Arrange
        var interceptor = new CountingWriteInterceptor();
        var context = CreateContext(interceptor);
        var root = new Person(context) { FirstName = "root" };
        interceptor.Arm(root, nameof(Person.Children));

        var staleChild = new Person { FirstName = "stale" };
        var replacementChild = new Person { FirstName = "replacement" };
        var descendant = new Person { FirstName = "descendant", Father = staleChild };
        var descendantScans = 0;
        AddStructuralGetter(descendant, "CaptureMarker", _ =>
        {
            Interlocked.Increment(ref descendantScans);
            return null;
        });

        var barrier = new CaptureBarrier(() => Volatile.Read(ref descendantScans) > 0);
        var sentinel = new Person { FirstName = "sentinel" };
        AddStructuralGetter(sentinel, "PauseAfterDescendant", barrier.Read);

        Exception? mutationException = null;
        var mutator = new Thread(() =>
        {
            barrier.WaitUntilReached();
            mutationException = Record.Exception(() => descendant.Father = replacementChild);
            barrier.Resume();
        }) { IsBackground = true };

        // Act
        mutator.Start();
        var writeException = Record.Exception(() => root.Children = [sentinel, descendant]);
        var mutationCompleted = mutator.Join(WriteProtocolAcceptance.RendezvousTimeout);

        // Assert
        Assert.True(mutationCompleted, "the descendant mutator never completed");
        Assert.True(barrier.PrerequisiteObserved,
            "the sentinel ran before the descendant's structural properties were captured");
        Assert.Null(mutationException);
        Assert.Null(writeException);
        Assert.Same(replacementChild, descendant.Father);
        Assert.Same(context, replacementChild.TryGetContext());
        Assert.Null(staleChild.TryGetContext());
        Assert.Equal(2, barrier.ReadCount);
        Assert.Equal(2, descendantScans);
        Assert.Equal(1, interceptor.Count);
    }

    [Fact]
    [Trait("Category", "Concurrency")]
    public void WhenOwnedDescendantDetachesAfterCapture_ThenAttachRecapturesItsOutgoingClosure()
    {
        // Arrange
        var context = CreateContext();
        var child = new Person { FirstName = "child" };
        var descendant = new Person { FirstName = "descendant", Father = child };
        descendant.AttachToContext(context);
        Assert.Same(context, child.TryGetContext());

        var barrier = new CaptureBarrier();
        var sentinel = new Person { FirstName = "sentinel" };
        AddStructuralGetter(sentinel, "PauseAfterOwnedDescendant", barrier.Read);
        var root = new EnumerableChildrenHolder { Children = [sentinel, descendant] };

        Exception? mutationException = null;
        var mutator = new Thread(() =>
        {
            barrier.WaitUntilReached();
            mutationException = Record.Exception(() => descendant.DetachFromContext(context));
            barrier.Resume();
        }) { IsBackground = true };

        // Act
        mutator.Start();
        var attachException = Record.Exception(() => ((IInterceptorSubject)root).AttachToContext(context));
        var mutationCompleted = mutator.Join(WriteProtocolAcceptance.RendezvousTimeout);

        // Assert
        Assert.True(mutationCompleted, "the descendant detacher never completed");
        Assert.Null(mutationException);
        Assert.Null(attachException);
        Assert.Same(context, root.TryGetContext());
        Assert.Same(context, descendant.TryGetContext());
        Assert.Same(context, child.TryGetContext());
        Assert.Equal(1, child.GetReferenceCount());
        Assert.Equal(2, barrier.ReadCount);
    }

    [Fact]
    [Trait("Category", "Concurrency")]
    public void WhenAdmissionRootChangesWhileANewGetterIsCaptured_ThenAdmissionRecapturesTheCurrentValue()
    {
        // Arrange
        var context = CreateContext();
        var root = new Person(context) { FirstName = "old" };
        var staleChild = new Person { FirstName = "stale" };
        var replacementChild = new Person { FirstName = "replacement" };
        var getterReached = new ManualResetEventSlim(false);
        var resumeGetter = new ManualResetEventSlim(false);
        var getterReads = 0;
        var metadata = new SubjectPropertyMetadata(
            "ProjectedChild",
            typeof(Person),
            [],
            subject =>
            {
                var captured = ((Person)subject).FirstName == "old" ? staleChild : replacementChild;
                if (Interlocked.Increment(ref getterReads) == 1)
                {
                    getterReached.Set();
                    if (!resumeGetter.Wait(WriteProtocolAcceptance.RendezvousTimeout))
                    {
                        throw new TimeoutException("Timed out waiting for the admission-root mutation.");
                    }
                }

                return captured;
            },
            null,
            isIntercepted: true,
            isDynamic: true);

        Exception? mutationException = null;
        var mutator = new Thread(() =>
        {
            if (!getterReached.Wait(WriteProtocolAcceptance.RendezvousTimeout))
            {
                mutationException = new TimeoutException("The new structural getter was never reached.");
                return;
            }

            mutationException = Record.Exception(() => root.FirstName = "new");
            resumeGetter.Set();
        }) { IsBackground = true };

        // Act
        mutator.Start();
        var admissionException = Record.Exception(() =>
            ((IInterceptorSubject)root).AddProperties(metadata));
        var mutationCompleted = mutator.Join(WriteProtocolAcceptance.RendezvousTimeout);

        // Assert
        Assert.True(mutationCompleted, "the admission-root mutator never completed");
        Assert.Null(mutationException);
        Assert.Null(admissionException);
        Assert.Equal("new", root.FirstName);
        Assert.Null(staleChild.TryGetContext());
        Assert.Same(context, replacementChild.TryGetContext());
        Assert.Equal(2, getterReads);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WhenAdmissionValidatesAHandWrittenRoot_ThenItDoesNotReenterInterfaceAccessors(
        bool throwFromExecutor)
    {
        // Arrange
        var context = CreateContext();
        var root = new GatedAccessorSubject();
        ((IInterceptorSubject)root).AttachToContext(context);
        root.Arm(throwFromExecutor);
        var metadata = new SubjectPropertyMetadata(
            "DynamicChild",
            typeof(IInterceptorSubject),
            [],
            _ => null,
            null,
            isIntercepted: true,
            isDynamic: true);

        // Act
        var exception = Record.Exception(() =>
            ((IInterceptorSubject)root).AddProperties(metadata));
        root.Disarm();

        // Assert
        Assert.Null(exception);
        Assert.True(((IInterceptorSubject)root).Properties.ContainsKey("DynamicChild"));
    }

    [Fact]
    public void WhenAdmissionAttachesAHandWrittenChild_ThenGraphPreparationUsesTheCapturedExecutor()
    {
        // Arrange
        var context = CreateContext();
        var root = new Person(context) { FirstName = "root" };
        var child = new GatedAccessorSubject();
        child.Arm(throwFromExecutor: true, allowedReads: 2);
        var metadata = new SubjectPropertyMetadata(
            "DynamicChild",
            typeof(IInterceptorSubject),
            [],
            _ => child,
            null,
            isIntercepted: true,
            isDynamic: true);

        // Act
        var exception = Record.Exception(() => ((IInterceptorSubject)root).AddProperties(metadata));
        child.Disarm();

        // Assert
        Assert.Null(exception);
        Assert.Same(context, child.TryGetContext());
        Assert.Equal(1, child.GetReferenceCount());
    }

    [Fact]
    [Trait("Category", "Concurrency")]
    public void WhenAdmissionClaimsMetadata_ThenScalarCommitWaitsForGraphPublication()
    {
        // Arrange
        var interceptor = new CountingWriteInterceptor();
        var context = CreateContext(interceptor);
        var root = new AdmissionClaimSubject();
        root.AttachToContext(context);
        interceptor.Arm(root, nameof(AdmissionClaimSubject.Count));
        var executor = (InterceptorExecutor)root.Executor;
        var child = new Person { FirstName = "child" };
        var writerBlocked = new ManualResetEventSlim(false);
        executor.CaptureMutationBlocked = writerBlocked;
        var publisherCalls = 0;
        var scalarObservedPublishedGraph = false;
        Exception? writerException = null;
        var writer = new Thread(() =>
        {
            writerException = Record.Exception(() => root.Count = 1);
            scalarObservedPublishedGraph = root.Properties.ContainsKey("DynamicChild") &&
                                           ReferenceEquals(child.TryGetContext(), context);
        }) { IsBackground = true };
        var registration = new SubjectPropertyRegistration(
            root,
            [new SubjectPropertyMetadata(
                "DynamicChild", typeof(Person), [], _ => child, null,
                isIntercepted: true, isDynamic: true)],
            properties =>
            {
                Interlocked.Increment(ref publisherCalls);
                root.PublishProperties(properties);
            })
        {
            BeforeTopologyPublication = () =>
            {
                writer.Start();
                if (!writerBlocked.Wait(WriteProtocolAcceptance.RendezvousTimeout))
                {
                    throw new TimeoutException("The scalar writer did not block on the publication claim.");
                }

                Assert.NotEqual(0, executor.CaptureRevision & 1);
            }
        };

        // Act
        var admissionException = Record.Exception(() => executor.AddProperties(registration));
        var writerCompleted = writer.Join(WriteProtocolAcceptance.RendezvousTimeout);

        // Assert
        Assert.True(writerCompleted, "the scalar writer remained blocked after admission");
        Assert.Null(admissionException);
        Assert.Null(writerException);
        Assert.True(scalarObservedPublishedGraph);
        Assert.Same(context, child.TryGetContext());
        Assert.Equal(1, publisherCalls);
        Assert.Equal(1, interceptor.Count);
    }

    [Fact]
    [Trait("Category", "Concurrency")]
    public void WhenUnrelatedGraphChangesAfterCapture_ThenAttachDoesNotRecaptureParticipants()
    {
        // Arrange
        var context = CreateContext();
        var unrelated = new Person(context) { FirstName = "unrelated" };
        var unrelatedChild = new Person { FirstName = "unrelated child" };

        var descendantScans = 0;
        var descendant = new Person { FirstName = "descendant" };
        AddStructuralGetter(descendant, "CaptureMarker", _ =>
        {
            Interlocked.Increment(ref descendantScans);
            return null;
        });

        var barrier = new CaptureBarrier(() => Volatile.Read(ref descendantScans) > 0);
        var sentinel = new Person { FirstName = "sentinel" };
        AddStructuralGetter(sentinel, "PauseAfterCandidate", barrier.Read);
        var root = new EnumerableChildrenHolder { Children = [sentinel, descendant] };

        Exception? mutationException = null;
        var mutator = new Thread(() =>
        {
            barrier.WaitUntilReached();
            mutationException = Record.Exception(() =>
            {
                unrelated.Father = unrelatedChild;
                ((IInterceptorSubject)unrelated).AddProperties(new SubjectPropertyMetadata(
                    "UnrelatedMarker",
                    typeof(int),
                    [],
                    _ => 1,
                    null,
                    isIntercepted: true,
                    isDynamic: true));
            });
            barrier.Resume();
        }) { IsBackground = true };

        // Act
        mutator.Start();
        var attachException = Record.Exception(() => ((IInterceptorSubject)root).AttachToContext(context));
        var mutationCompleted = mutator.Join(WriteProtocolAcceptance.RendezvousTimeout);

        // Assert
        Assert.True(mutationCompleted, "the unrelated writer never completed");
        Assert.True(barrier.PrerequisiteObserved,
            "the sentinel ran before the candidate descendant was captured");
        Assert.Null(mutationException);
        Assert.Null(attachException);
        Assert.Same(context, root.TryGetContext());
        Assert.Same(context, descendant.TryGetContext());
        Assert.Same(context, unrelatedChild.TryGetContext());
        Assert.True(((IInterceptorSubject)unrelated).Properties.ContainsKey("UnrelatedMarker"));
        Assert.Equal(1, barrier.ReadCount);
        Assert.Equal(1, descendantScans);
    }

    private static void AddStructuralGetter(
        IInterceptorSubject subject,
        string name,
        Func<IInterceptorSubject, object?> getter)
    {
        subject.AddProperties(new SubjectPropertyMetadata(
            name,
            typeof(Person),
            [],
            getter,
            null,
            isIntercepted: true,
            isDynamic: true));
    }

    private sealed class CaptureBarrier(Func<bool>? prerequisite = null)
    {
        private readonly ManualResetEventSlim _reached = new(false);
        private readonly ManualResetEventSlim _resume = new(false);
        private int _readCount;

        internal int ReadCount => Volatile.Read(ref _readCount);

        internal bool PrerequisiteObserved { get; private set; }

        internal object? Read(IInterceptorSubject _)
        {
            if (Interlocked.Increment(ref _readCount) != 1)
            {
                return null;
            }

            PrerequisiteObserved = prerequisite?.Invoke() ?? true;
            _reached.Set();
            if (!_resume.Wait(WriteProtocolAcceptance.RendezvousTimeout))
            {
                throw new TimeoutException("Timed out waiting for the capture mutation.");
            }

            return null;
        }

        internal void WaitUntilReached()
        {
            if (!_reached.Wait(WriteProtocolAcceptance.RendezvousTimeout))
            {
                throw new TimeoutException("Timed out waiting for the post-descendant capture point.");
            }
        }

        internal void Resume() => _resume.Set();
    }

    private sealed class CountingWriteInterceptor : IWriteInterceptor
    {
        private IInterceptorSubject? _target;
        private string? _propertyName;
        private int _count;

        internal int Count => Volatile.Read(ref _count);

        internal void Arm(IInterceptorSubject target, string propertyName)
        {
            _target = target;
            _propertyName = propertyName;
            Volatile.Write(ref _count, 0);
        }

        public void WriteProperty<TProperty>(
            ref PropertyWriteContext<TProperty> context,
            WriteInterceptionDelegate<TProperty> next)
        {
            if (ReferenceEquals(context.Property.Subject, _target) && context.Property.Name == _propertyName)
            {
                Interlocked.Increment(ref _count);
            }

            next(ref context);
        }
    }

    private sealed class GatedAccessorSubject : IInterceptorSubject
    {
        private IInterceptorExecutor? _executor;
        private IReadOnlyDictionary<string, SubjectPropertyMetadata> _properties =
            new Dictionary<string, SubjectPropertyMetadata>();
        private int _executorReads;
        private int _propertyReads;
        private int _armed;
        private bool _throwFromExecutor;
        private int _allowedReads;

        public IInterceptorExecutor Executor
        {
            get
            {
                if (Volatile.Read(ref _armed) != 0 &&
                    Interlocked.Increment(ref _executorReads) > _allowedReads &&
                    _throwFromExecutor)
                {
                    throw new InvalidOperationException("The executor accessor was reentered during admission.");
                }

                return InterceptorExecutor.GetOrCreate(ref _executor, this);
            }
        }

        public ConcurrentDictionary<(string? property, string key), object?> Data { get; } = new();

        public IReadOnlyDictionary<string, SubjectPropertyMetadata> Properties
        {
            get
            {
                if (Volatile.Read(ref _armed) != 0 &&
                    Interlocked.Increment(ref _propertyReads) > 3 &&
                    !_throwFromExecutor)
                {
                    throw new InvalidOperationException("The properties accessor was reentered during admission.");
                }

                return _properties;
            }
        }

        public void AddProperties(params IEnumerable<SubjectPropertyMetadata> properties) =>
            Executor.AddProperties(new SubjectPropertyRegistration(
                this,
                properties,
                published => _properties = published));

        internal void Arm(bool throwFromExecutor, int allowedReads = 3)
        {
            _throwFromExecutor = throwFromExecutor;
            _allowedReads = allowedReads;
            Volatile.Write(ref _executorReads, 0);
            Volatile.Write(ref _propertyReads, 0);
            Volatile.Write(ref _armed, 1);
        }

        internal void Disarm() => Volatile.Write(ref _armed, 0);
    }

    private sealed class AdmissionClaimSubject : IInterceptorSubject
    {
        private IInterceptorExecutor? _executor;
        private IReadOnlyDictionary<string, SubjectPropertyMetadata> _properties =
            new Dictionary<string, SubjectPropertyMetadata>
            {
                [nameof(Count)] = new(
                    nameof(Count), typeof(int), [],
                    subject => ((AdmissionClaimSubject)subject)._count,
                    (subject, value) => ((AdmissionClaimSubject)subject).Count = (int)value!,
                    isIntercepted: true, isDynamic: false)
            };
        private int _count;

        public IInterceptorExecutor Executor => InterceptorExecutor.GetOrCreate(ref _executor, this);

        public ConcurrentDictionary<(string? property, string key), object?> Data { get; } = new();

        public IReadOnlyDictionary<string, SubjectPropertyMetadata> Properties =>
            Volatile.Read(ref _properties);

        public void AddProperties(params IEnumerable<SubjectPropertyMetadata> properties) =>
            Executor.AddProperties(new SubjectPropertyRegistration(this, properties, PublishProperties));

        public int Count
        {
            get => Executor.GetPropertyValue(nameof(Count), subject => ((AdmissionClaimSubject)subject)._count);
            set => Executor.SetPropertyValue(nameof(Count), value, _count,
                (subject, newValue) => ((AdmissionClaimSubject)subject)._count = newValue);
        }

        internal void PublishProperties(IReadOnlyDictionary<string, SubjectPropertyMetadata> properties) =>
            Volatile.Write(ref _properties, properties);
    }
}
