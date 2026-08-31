using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Reflection;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tests;

/// <summary>
/// Pins the write protocol for subjects written by hand instead of generated: their setters call
/// the one <see cref="IInterceptorExecutor.SetPropertyValue{TProperty}"/> entry, and the runtime
/// routing must give a subject-typed property the structural protocol (the lifecycle gate before
/// the chain) without the author choosing an accessor.
/// </summary>
public class HandWrittenSubjectWriteTests
{
    private static readonly TimeSpan RendezvousTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// A minimal faithful lifecycle that counts gate entries and chain executions, so a test can
    /// observe on which side of the structural routing a write ran.
    /// </summary>
    private sealed class GateCountingLifecycle : ILifecycleInterceptor
    {
        private readonly object _structuralWriteGate = new();

        public int GateEnterCount;
        public int WritePropertyCount;

        public void EnterStructuralWriteGate()
        {
            Monitor.Enter(_structuralWriteGate);
            Interlocked.Increment(ref GateEnterCount);
        }

        public void ExitStructuralWriteGate() => Monitor.Exit(_structuralWriteGate);

        public bool TryAddProperties(SubjectPropertyRegistration registration)
        {
            registration.Publish();
            return true;
        }

        public void AttachSubjectToContext(IInterceptorSubject subject, IInterceptorSubjectContext context, SubjectAttachmentAnchorKind anchor)
        {
            InterceptorSubjectExtensions.ApplyRootAnchor(subject, context, anchor);
        }

        public void DetachSubjectFromContext(IInterceptorSubject subject, IInterceptorSubjectContext context)
        {
            var executor = subject.Executor;
            executor.TryGetAttachment(out var attachedContext, out var anchor, out var revision);
            InterceptorSubjectExtensions.ValidateDetach(attachedContext, anchor, context);
            executor.TryUpdateAttachment(revision, attachedContext, SubjectAttachmentAnchorKind.None, out _);
        }

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            Interlocked.Increment(ref WritePropertyCount);
            next(ref context);
        }
    }

    /// <summary>
    /// A subject implemented by hand, the way a consumer without the source generator writes one:
    /// every setter calls SetPropertyValue, whatever the property type.
    /// </summary>
    private sealed class HandWrittenSubject : IInterceptorSubject
    {
        private static readonly FrozenDictionary<string, SubjectPropertyMetadata> Metadata =
            new Dictionary<string, SubjectPropertyMetadata>
            {
                [nameof(Child)] = new(
                    nameof(Child),
                    typeof(HandWrittenSubject),
                    [],
                    static subject => ((HandWrittenSubject)subject)._child,
                    static (subject, value) => ((HandWrittenSubject)subject).Child = (HandWrittenSubject?)value,
                    isIntercepted: true,
                    isDynamic: false),
                [nameof(Count)] = new(
                    nameof(Count),
                    typeof(int),
                    [],
                    static subject => ((HandWrittenSubject)subject)._count,
                    static (subject, value) => ((HandWrittenSubject)subject).Count = (int)value!,
                    isIntercepted: true,
                    isDynamic: false)
            }.ToFrozenDictionary();

        private IInterceptorExecutor? _executor;
        private HandWrittenSubject? _child;
        private int _count;

        public IInterceptorExecutor Executor => InterceptorExecutor.GetOrCreate(ref _executor, this);

        public ConcurrentDictionary<(string? property, string key), object?> Data { get; } = new();

        public IReadOnlyDictionary<string, SubjectPropertyMetadata> Properties => Metadata;

        public void AddProperties(params IEnumerable<SubjectPropertyMetadata> properties) =>
            throw new NotSupportedException("The hand-written subject declares all its properties statically.");

        public HandWrittenSubject? Child
        {
            get => Executor.GetPropertyValue(nameof(Child), static subject => ((HandWrittenSubject)subject)._child);
            set => Executor.SetPropertyValue(nameof(Child), value, _child,
                static (subject, newValue) => ((HandWrittenSubject)subject)._child = newValue);
        }

        public int Count
        {
            get => Executor.GetPropertyValue(nameof(Count), static subject => ((HandWrittenSubject)subject)._count);
            set => Executor.SetPropertyValue(nameof(Count), value, _count,
                static (subject, newValue) => ((HandWrittenSubject)subject)._count = newValue);
        }
    }

    private sealed class DynamicHandWrittenSubject : IInterceptorSubject
    {
        private IInterceptorExecutor? _executor;
        private IReadOnlyDictionary<string, SubjectPropertyMetadata> _properties =
            new Dictionary<string, SubjectPropertyMetadata>
            {
                [nameof(Count)] = new(
                    nameof(Count), typeof(int), [],
                    subject => ((DynamicHandWrittenSubject)subject)._count,
                    (subject, value) => ((DynamicHandWrittenSubject)subject).Count = (int)value!,
                    isIntercepted: true, isDynamic: false)
            };
        private int _count;
        private int _propertyReads;

        internal ManualResetEventSlim? PreparationReached { get; set; }
        internal ManualResetEventSlim? ResumePreparation { get; set; }
        internal int PublisherCalls { get; private set; }

        public IInterceptorExecutor Executor => InterceptorExecutor.GetOrCreate(ref _executor, this);

        public ConcurrentDictionary<(string? property, string key), object?> Data { get; } = new();

        public IReadOnlyDictionary<string, SubjectPropertyMetadata> Properties
        {
            get
            {
                if (Interlocked.Increment(ref _propertyReads) == 2 && PreparationReached is { } reached)
                {
                    reached.Set();
                    if (ResumePreparation?.Wait(RendezvousTimeout) != true)
                    {
                        throw new TimeoutException("Timed out waiting for the concurrent scalar write.");
                    }
                }

                return Volatile.Read(ref _properties);
            }
        }

        public void AddProperties(params IEnumerable<SubjectPropertyMetadata> properties) =>
            Executor.AddProperties(new SubjectPropertyRegistration(this, properties, published =>
            {
                PublisherCalls++;
                Volatile.Write(ref _properties, published);
            }));

        public int Count
        {
            get => Executor.GetPropertyValue(nameof(Count), subject => ((DynamicHandWrittenSubject)subject)._count);
            set => Executor.SetPropertyValue(nameof(Count), value, _count,
                (subject, newValue) => ((DynamicHandWrittenSubject)subject)._count = newValue);
        }
    }

    private sealed class CountingMetadataSequence(SubjectPropertyMetadata metadata) :
        IEnumerable<SubjectPropertyMetadata>
    {
        internal int EnumerationCount { get; private set; }

        public IEnumerator<SubjectPropertyMetadata> GetEnumerator()
        {
            EnumerationCount++;
            yield return metadata;
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Fact]
    public void WhenHandWrittenSetterAssignsASubjectTypedProperty_ThenTheWriteTakesTheLifecycleGate()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var probe = new GateCountingLifecycle();
        context.AddService(probe);
        var subject = new HandWrittenSubject();
        ((IInterceptorSubject)subject).AttachToContext(context);
        var child = new HandWrittenSubject();

        // Act
        subject.Child = child;

        // Assert: the write entered the structural protocol (gate before the chain), not just the
        // chain. A write that skips the gate still runs the chain, so both counters are needed.
        Assert.Equal(1, probe.GateEnterCount);
        Assert.Equal(1, probe.WritePropertyCount);
        Assert.Same(child, subject.Child);
    }

    [Fact]
    public void WhenHandWrittenSetterAssignsAScalarProperty_ThenTheWriteTakesNoLifecycleGate()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var probe = new GateCountingLifecycle();
        context.AddService(probe);
        var subject = new HandWrittenSubject();
        ((IInterceptorSubject)subject).AttachToContext(context);

        // Act
        subject.Count = 42;

        // Assert: the scalar route never pays for the structural protocol.
        Assert.Equal(0, probe.GateEnterCount);
        Assert.Equal(1, probe.WritePropertyCount);
        Assert.Equal(42, subject.Count);
    }

    [Fact]
    public void WhenDetachedManualAndGeneratedStructuralSettersRun_ThenOnlyTheGeneratedEntryConsumesTerminalState()
    {
        // Arrange
        var manual = new HandWrittenSubject();
        var generated = new StructuralHolder();

        // Act
        manual.Child = new HandWrittenSubject();
        generated.Child = new StructuralHolder();

        // Assert
        var manualExecutor = Assert.IsType<InterceptorExecutor>(((IInterceptorSubject)manual).Executor);
        Assert.Equal(0, manualExecutor.Revision);
        Assert.False(new PropertyReference(manual, nameof(HandWrittenSubject.Child))
            .TryGetWriteState(true, out _, out _));

        var generatedExecutor = Assert.IsType<InterceptorExecutor>(((IInterceptorSubject)generated).Executor);
        Assert.Equal(1, generatedExecutor.Revision);
        Assert.True(new PropertyReference(generated, nameof(StructuralHolder.Child))
            .TryGetWriteState(true, out var generatedRevision, out _));
        Assert.Equal(1, generatedRevision);
    }

    [Fact]
    [Trait("Category", "Concurrency")]
    public void WhenLifecycleFreeMetadataPreparationRacesAScalarWrite_ThenItRetriesWithoutReplayingInputOrPublisher()
    {
        // Arrange
        var subject = new DynamicHandWrittenSubject();
        var reached = new ManualResetEventSlim(false);
        var resume = new ManualResetEventSlim(false);
        subject.PreparationReached = reached;
        subject.ResumePreparation = resume;
        var sequence = new CountingMetadataSequence(new SubjectPropertyMetadata(
            "Dynamic", typeof(string), [], _ => "value", null,
            isIntercepted: true, isDynamic: true));
        Exception? writerException = null;
        var writer = new Thread(() =>
        {
            if (!reached.Wait(RendezvousTimeout))
            {
                writerException = new TimeoutException("Metadata preparation was never reached.");
                resume.Set();
                return;
            }

            writerException = Record.Exception(() => subject.Count = 1);
            resume.Set();
        }) { IsBackground = true };

        // Act
        writer.Start();
        var admissionException = Record.Exception(() => subject.AddProperties(sequence));
        var writerCompleted = writer.Join(RendezvousTimeout);

        // Assert
        Assert.True(writerCompleted, "the scalar writer did not complete");
        Assert.Null(writerException);
        Assert.Null(admissionException);
        Assert.Equal(1, sequence.EnumerationCount);
        Assert.Equal(1, subject.PublisherCalls);
        Assert.True(subject.Properties.ContainsKey("Dynamic"));
        Assert.Equal(1, subject.Count);
    }

    [Fact]
    public void WhenSameThreadCaptureRefreshCrossesRollover_ThenTheContinuousRunIsAccepted()
    {
        // Arrange
        var subject = new DynamicHandWrittenSubject();
        var executor = (InterceptorExecutor)subject.Executor;
        var capturedRevision = long.MaxValue - 1;
        SetCaptureState(executor, capturedRevision, Environment.CurrentManagedThreadId, long.MaxValue - 3);

        // Act
        subject.Count = 1;
        var refreshed = executor.TryRefreshCapture(capturedRevision, out var currentRevision);

        // Assert
        Assert.True(refreshed);
        Assert.Equal(long.MinValue, currentRevision);
    }

    [Fact]
    public void WhenForeignWriterCrossesCaptureRollover_ThenTheRunIsRejected()
    {
        // Arrange
        var subject = new DynamicHandWrittenSubject();
        var executor = (InterceptorExecutor)subject.Executor;
        var capturedRevision = long.MaxValue - 1;
        SetCaptureState(executor, capturedRevision, Environment.CurrentManagedThreadId, long.MaxValue - 3);
        Exception? writerException = null;
        var writer = new Thread(() => writerException = Record.Exception(() => subject.Count = 1))
        {
            IsBackground = true
        };

        // Act
        writer.Start();
        var writerCompleted = writer.Join(RendezvousTimeout);
        var refreshed = executor.TryRefreshCapture(capturedRevision, out var currentRevision);

        // Assert
        Assert.True(writerCompleted, "the foreign writer did not complete");
        Assert.Null(writerException);
        Assert.False(refreshed);
        Assert.Equal(long.MinValue, currentRevision);
    }

    private static void SetCaptureState(
        InterceptorExecutor executor,
        long revision,
        int writerThreadId,
        long writerRunStart)
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(InterceptorExecutor).GetField("_captureRevision", flags)!.SetValue(executor, revision);
        typeof(InterceptorExecutor).GetField("_captureWriterThreadId", flags)!.SetValue(executor, writerThreadId);
        typeof(InterceptorExecutor).GetField("_captureWriterRunStart", flags)!.SetValue(executor, writerRunStart);
    }
}
