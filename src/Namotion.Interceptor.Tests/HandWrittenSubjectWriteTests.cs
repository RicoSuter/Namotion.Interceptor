using System.Collections.Concurrent;
using System.Collections.Frozen;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tests;

/// <summary>
/// Pins the write protocol for subjects written by hand instead of generated: their setters call
/// the one <see cref="IInterceptorExecutor.SetPropertyValue{TProperty}"/> entry, and the runtime
/// routing must give a subject-typed property the structural protocol without the author choosing
/// an accessor.
/// </summary>
public class HandWrittenSubjectWriteTests
{
    /// <summary>
    /// A minimal faithful lifecycle that records its chain executions, the compile-time property
    /// type of each, and whether the commit happened inside its next() frame, so a test can
    /// observe which route a write took and that the lifecycle sits terminal-adjacent in the
    /// compiled chain.
    /// </summary>
    private sealed class ChainObservingLifecycle : ILifecycleInterceptor
    {
        public readonly List<Type> WrittenPropertyTypes = [];
        public readonly List<bool> CommitObservations = [];
        public List<string>? ExecutionLog;

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
            ExecutionLog?.Add("lifecycle");
            WrittenPropertyTypes.Add(typeof(TProperty));
            next(ref context);
            CommitObservations.Add(context.IsWritten);
        }
    }

    private sealed class OrderRecordingInterceptor(List<string> log) : IWriteInterceptor
    {
        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            log.Add("recorder");
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

    [Fact]
    public void WhenHandWrittenSetterAssignsASubjectTypedProperty_ThenTheLifecycleRunsLastAndWrapsTheCommit()
    {
        // Arrange: the recorder registers after the lifecycle and carries no ordering attributes,
        // so the resolver leaves it after the lifecycle; the chain partition must still execute
        // it upstream, keeping the lifecycle terminal-adjacent.
        var context = InterceptorSubjectContext.Create();
        var log = new List<string>();
        var probe = new ChainObservingLifecycle { ExecutionLog = log };
        context.AddService(probe);
        context.AddService<IWriteInterceptor>(new OrderRecordingInterceptor(log));
        var subject = new HandWrittenSubject();
        ((IInterceptorSubject)subject).AttachToContext(context);
        var child = new HandWrittenSubject();

        // Act
        subject.Child = child;

        // Assert: the subject-typed write ran the chain with the declared type (the structural
        // route), the lifecycle executed last despite the recorder's later registration, and the
        // commit happened inside the lifecycle's next() frame, which is the structural protocol's
        // synchronization seam.
        Assert.Equal(["recorder", "lifecycle"], log);
        Assert.Equal([typeof(HandWrittenSubject)], probe.WrittenPropertyTypes);
        Assert.Equal([true], probe.CommitObservations);
        Assert.Same(child, subject.Child);
    }

    [Fact]
    public void WhenHandWrittenSetterAssignsAScalarProperty_ThenTheWriteRunsTheChainOnTheScalarRoute()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var probe = new ChainObservingLifecycle();
        context.AddService(probe);
        var subject = new HandWrittenSubject();
        ((IInterceptorSubject)subject).AttachToContext(context);

        // Act
        subject.Count = 42;

        // Assert: the write ran the chain once with the scalar compile-time type, which is what
        // routes it off the structural protocol; the declared-type classification for narrowed
        // writes is the lifecycle's own job inside the chain.
        Assert.Equal([typeof(int)], probe.WrittenPropertyTypes);
        Assert.Equal([true], probe.CommitObservations);
        Assert.Equal(42, subject.Count);
    }
}
