using System.Collections.Concurrent;
using System.Reflection;
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
    public void WhenScalarCommitTemporarilyInvalidatesCapture_ThenStructuralWriteRetriesWithoutSurfacingConflict()
    {
        // Arrange
        var context = CreateContext();
        var root = new CaptureBlockingHolder();
        root.AttachToContext(context);
        var child = new CaptureBlockingSubject();
        child.AttachToContext(context);
        child.BlockNextWrite();
        Exception? scalarException = null;
        Exception? structuralException = null;
        var scalarWriter = new Thread(() =>
        {
            scalarException = Record.Exception(() => child.Count = 1);
        }) { IsBackground = true };
        var structuralWriter = new Thread(() =>
        {
            structuralException = Record.Exception(() => root.Child = child);
        }) { IsBackground = true };

        // Act
        scalarWriter.Start();
        Assert.True(
            child.WriteEntered.Wait(WriteProtocolAcceptance.RendezvousTimeout),
            "the scalar writer did not enter its raw terminal");
        child.ObserveNextCapture();
        structuralWriter.Start();
        Assert.True(
            child.CaptureObserved.Wait(WriteProtocolAcceptance.RendezvousTimeout),
            "the structural writer did not observe the in-progress scalar commit");
        child.ContinueWrite.Set();
        var scalarCompleted = scalarWriter.Join(WriteProtocolAcceptance.RendezvousTimeout);
        var structuralCompleted = structuralWriter.Join(WriteProtocolAcceptance.RendezvousTimeout);

        // Assert
        Assert.True(scalarCompleted, "the scalar writer did not complete");
        Assert.True(structuralCompleted, "the structural writer did not complete");
        Assert.Null(scalarException);
        Assert.Null(structuralException);
        Assert.Equal(1, child.Count);
        Assert.Same(context, child.TryGetContext());
        Assert.Same(child, root.Child);
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
    public void WhenAdmissionReservesMetadata_ThenConcurrentScalarCommitCanRetryAfterPublication()
    {
        // Arrange
        var context = CreateContext();
        var root = new AdmissionClaimSubject();
        root.AttachToContext(context);
        var executor = (InterceptorExecutor)root.Executor;
        var child = new Person { FirstName = "child" };
        var conflictObserved = new ManualResetEventSlim(false);
        var resumeWriter = new ManualResetEventSlim(false);
        var publisherCalls = 0;
        var scalarObservedPublishedGraph = false;
        Exception? conflictException = null;
        Exception? writerException = null;
        var writer = new Thread(() =>
        {
            conflictException = Record.Exception(() => root.Count = 1);
            conflictObserved.Set();
            if (!resumeWriter.Wait(WriteProtocolAcceptance.RendezvousTimeout))
            {
                writerException = new TimeoutException("Timed out waiting to retry the scalar write.");
                return;
            }

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
                writer.Start();
                if (!conflictObserved.Wait(WriteProtocolAcceptance.RendezvousTimeout))
                {
                    throw new TimeoutException("The concurrent scalar write did not report its reservation conflict.");
                }
            });

        // Act
        var admissionException = Record.Exception(() => executor.AddProperties(registration));
        resumeWriter.Set();
        var writerCompleted = writer.Join(WriteProtocolAcceptance.RendezvousTimeout);

        // Assert
        Assert.True(writerCompleted, "the scalar writer did not complete its retry after admission");
        Assert.Null(admissionException);
        Assert.IsType<LifecycleConflictException>(conflictException);
        Assert.Null(writerException);
        Assert.True(scalarObservedPublishedGraph);
        Assert.Same(context, child.TryGetContext());
        Assert.Equal(1, root.Count);
        Assert.Equal(1, publisherCalls);
    }

    [Fact]
    [Trait("Category", "Concurrency")]
    public void WhenAdmissionReservesCapturedChild_ThenConcurrentScalarCommitCanRetryAfterPublication()
    {
        // Arrange
        var context = CreateContext();
        var root = new AdmissionClaimSubject();
        root.AttachToContext(context);
        var child = new AdmissionClaimSubject();
        child.AttachToContext(context, SubjectAttachmentAnchorKind.Provisional);
        var conflictObserved = new ManualResetEventSlim(false);
        var resumeWriter = new ManualResetEventSlim(false);
        var publisherCalls = 0;
        Exception? conflictException = null;
        Exception? writerException = null;
        var writer = new Thread(() =>
        {
            conflictException = Record.Exception(() => child.Count = 1);
            conflictObserved.Set();
            if (!resumeWriter.Wait(WriteProtocolAcceptance.RendezvousTimeout))
            {
                writerException = new TimeoutException("Timed out waiting to retry the child scalar write.");
                return;
            }

            writerException = Record.Exception(() => child.Count = 1);
        }) { IsBackground = true };
        var registration = new SubjectPropertyRegistration(
            root,
            [new SubjectPropertyMetadata(
                "DynamicChild", typeof(AdmissionClaimSubject), [], _ => child, null,
                isIntercepted: true, isDynamic: true)],
            properties =>
            {
                Interlocked.Increment(ref publisherCalls);
                root.PublishProperties(properties);
                writer.Start();
                if (!conflictObserved.Wait(WriteProtocolAcceptance.RendezvousTimeout))
                {
                    throw new TimeoutException("The child scalar write did not report its reservation conflict.");
                }
            });

        // Act
        var admissionException = Record.Exception(() => root.Executor.AddProperties(registration));
        resumeWriter.Set();
        var writerCompleted = writer.Join(WriteProtocolAcceptance.RendezvousTimeout);

        // Assert
        Assert.True(writerCompleted, "the child scalar writer did not complete its retry after admission");
        Assert.Null(admissionException);
        Assert.IsType<LifecycleConflictException>(conflictException);
        Assert.Null(writerException);
        Assert.True(root.Properties.ContainsKey("DynamicChild"));
        Assert.Same(context, child.TryGetContext());
        Assert.Equal(1, child.GetReferenceCount());
        Assert.Equal(1, child.Count);
        Assert.Equal(1, publisherCalls);
    }

    [Fact]
    public void WhenAttachedAdmissionInvokesItsPublisher_ThenNoTopologyGateIsHeld()
    {
        // Arrange
        var context = CreateContext();
        var lifecycle = context.GetService<LifecycleInterceptor>();
        var root = new AdmissionClaimSubject();
        root.AttachToContext(context);
        var gate = Assert.IsType<Lock>(typeof(LifecycleInterceptor)
            .GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(lifecycle));
        var publisherHeldGate = false;
        var registration = new SubjectPropertyRegistration(
            root,
            [new SubjectPropertyMetadata(
                "Dynamic", typeof(string), [], _ => "value", null,
                isIntercepted: true, isDynamic: true)],
            properties =>
            {
                publisherHeldGate = gate.IsHeldByCurrentThread;
                root.PublishProperties(properties);
            });

        // Act
        root.Executor.AddProperties(registration);

        // Assert
        Assert.False(publisherHeldGate);
        Assert.True(root.Properties.ContainsKey("Dynamic"));
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

    private sealed class CaptureBlockingSubject : IInterceptorSubject
    {
        private IInterceptorExecutor? _executor;
        private readonly IReadOnlyDictionary<string, SubjectPropertyMetadata> _properties;
        private int _count;
        private int _blockNextWrite;
        private int _observeNextCapture;

        internal CaptureBlockingSubject()
        {
            _properties = new Dictionary<string, SubjectPropertyMetadata>
            {
                [nameof(Count)] = new(
                    nameof(Count), typeof(int), [],
                    subject => ((CaptureBlockingSubject)subject)._count,
                    (subject, value) => ((CaptureBlockingSubject)subject).Count = (int)value!,
                    isIntercepted: true, isDynamic: false)
            };
        }

        public IInterceptorExecutor Executor => InterceptorExecutor.GetOrCreate(ref _executor, this);

        public ConcurrentDictionary<(string? property, string key), object?> Data { get; } = new();

        public IReadOnlyDictionary<string, SubjectPropertyMetadata> Properties
        {
            get
            {
                if (Interlocked.Exchange(ref _observeNextCapture, 0) == 1)
                {
                    CaptureObserved.Set();
                }

                return _properties;
            }
        }

        public void AddProperties(params IEnumerable<SubjectPropertyMetadata> properties) =>
            throw new NotSupportedException();

        internal ManualResetEventSlim WriteEntered { get; } = new(false);

        internal ManualResetEventSlim ContinueWrite { get; } = new(false);

        internal ManualResetEventSlim CaptureObserved { get; } = new(false);

        internal int Count
        {
            get => Executor.GetPropertyValue(nameof(Count), subject => ((CaptureBlockingSubject)subject)._count);
            set => Executor.SetPropertyValue(nameof(Count), value, _count, (subject, newValue) =>
            {
                var target = (CaptureBlockingSubject)subject;
                if (Interlocked.Exchange(ref target._blockNextWrite, 0) == 1)
                {
                    target.WriteEntered.Set();
                    if (!target.ContinueWrite.Wait(WriteProtocolAcceptance.RendezvousTimeout))
                    {
                        throw new TimeoutException("Timed out waiting to finish the scalar terminal.");
                    }
                }

                target._count = newValue;
            });
        }

        internal void BlockNextWrite() => Volatile.Write(ref _blockNextWrite, 1);

        internal void ObserveNextCapture() => Volatile.Write(ref _observeNextCapture, 1);
    }

    private sealed class CaptureBlockingHolder : IInterceptorSubject
    {
        private IInterceptorExecutor? _executor;
        private CaptureBlockingSubject? _child;

        public IInterceptorExecutor Executor => InterceptorExecutor.GetOrCreate(ref _executor, this);

        public ConcurrentDictionary<(string? property, string key), object?> Data { get; } = new();

        public IReadOnlyDictionary<string, SubjectPropertyMetadata> Properties { get; } =
            new Dictionary<string, SubjectPropertyMetadata>
            {
                [nameof(Child)] = new(
                    nameof(Child), typeof(CaptureBlockingSubject), [],
                    subject => ((CaptureBlockingHolder)subject)._child,
                    (subject, value) => ((CaptureBlockingHolder)subject).Child = (CaptureBlockingSubject?)value,
                    isIntercepted: true, isDynamic: false)
            };

        public void AddProperties(params IEnumerable<SubjectPropertyMetadata> properties) =>
            throw new NotSupportedException();

        internal CaptureBlockingSubject? Child
        {
            get => Executor.GetPropertyValue(
                nameof(Child), subject => ((CaptureBlockingHolder)subject)._child);
            set => ((InterceptorExecutor)Executor).SetGeneratedPropertyValue(
                nameof(Child), value,
                subject => ((CaptureBlockingHolder)subject)._child,
                (subject, newValue) => ((CaptureBlockingHolder)subject)._child = newValue);
        }
    }
}
