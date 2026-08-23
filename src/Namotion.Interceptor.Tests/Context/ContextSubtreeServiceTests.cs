using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Tests.Context;

/// <summary>
/// A subject may register services of its own. They apply to that subject and nothing else: a
/// subject resolves through the one context it is attached to, not through the subject that
/// referenced it, so a service registered on one subject never reaches another.
///
/// Scoping such a service to the subject's whole subtree used to fall out of the resolution rules,
/// because a subject resolved through the context of the parent that first referenced it. That is
/// removed together with exact-context ownership: the parent chain it depended on could be taken
/// apart while the subject was still attached, was as deep as the graph, and closed a resolution
/// loop whenever two subjects each became the other's first parent.
/// </summary>
public class ContextSubtreeServiceTests
{
    [Fact]
    public void WhenSubjectRegistersOwnInterceptor_ThenItAppliesToThatSubjectOnly()
    {
        // Arrange: two independent subtrees under one shared root, only one of which brings its own
        // interceptor.
        var rootContext = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        var rootSensor = new SubtreeSensor(rootContext);

        var ownedInterceptor = new CountingWriteInterceptor();
        var ownedSensor = new SubtreeSensor { Name = "owned" };
        var ownedChild = new SubtreeSensor { Name = "owned child" };

        var foreignSensor = new SubtreeSensor { Name = "foreign" };
        var foreignChild = new SubtreeSensor { Name = "foreign child" };

        rootSensor.Child = ownedSensor;
        ownedSensor.Child = ownedChild;

        // Attached second so that the interceptor is registered on a subject that is already part
        // of the graph, which is how a component that is loaded later would do it.
        ((IInterceptorSubject)ownedSensor).Context.AddService<IWriteInterceptor>(ownedInterceptor);

        // Act
        ownedSensor.Value = 1;
        ownedChild.Value = 2;

        // Assert: only the subject that registered it. Its child resolves through the context the
        // graph is attached to, not through its parent subject.
        Assert.Equal(1, ownedInterceptor.WriteCount);

        // Act: a sibling subtree of the same root, which the interceptor must not reach either.
        rootSensor.Child = foreignSensor;
        foreignSensor.Child = foreignChild;
        foreignSensor.Value = 3;
        foreignChild.Value = 4;

        // Assert
        Assert.Equal(1, ownedInterceptor.WriteCount);
        Assert.Equal(3, foreignSensor.Value);
        Assert.Equal(4, foreignChild.Value);
    }

    /// <summary>
    /// The services of the subject come before the services of the context it was attached to, so a
    /// component's own interceptor wraps the ones the graph already had rather than the other way
    /// round.
    /// </summary>
    [Fact]
    public void WhenSubjectRegistersOwnInterceptor_ThenItRunsBeforeTheOnesOfTheParentContext()
    {
        // Arrange
        var order = new List<string>();
        var rootInterceptor = new RecordingWriteInterceptor("root", order);

        var rootContext = InterceptorSubjectContext.Create();
        rootContext.AddService<IWriteInterceptor>(rootInterceptor);

        var subject = new SubtreeSensor(rootContext);
        ((IInterceptorSubject)subject).Context.AddService<IWriteInterceptor>(new RecordingWriteInterceptor("subject", order));

        // Act
        subject.Value = 1;

        // Assert
        Assert.Equal(["subject", "root"], order);
    }

    /// <summary>
    /// Registering a service turns a subject that only delegated into one that answers. Its own
    /// resolution has to notice; a subject it merely references does not, because that subject
    /// resolves through the context rather than through it.
    /// </summary>
    [Fact]
    public void WhenSubjectRegistersOwnInterceptorAfterResolving_ThenOnlyItsOwnResolutionNotices()
    {
        // Arrange
        var rootContext = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        var parent = new SubtreeSensor(rootContext);
        var child = new SubtreeSensor();
        parent.Child = child;

        // Resolves and caches the chain of both subjects while neither has services.
        parent.Value = 1;
        child.Value = 1;

        var interceptor = new CountingWriteInterceptor();

        // Act
        ((IInterceptorSubject)parent).Context.AddService<IWriteInterceptor>(interceptor);
        parent.Value = 2;
        child.Value = 2;

        // Assert
        Assert.Equal(1, interceptor.WriteCount);
    }

    private sealed class CountingWriteInterceptor : IWriteInterceptor
    {
        private int _writeCount;

        internal int WriteCount => Volatile.Read(ref _writeCount);

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            Interlocked.Increment(ref _writeCount);
            next(ref context);
        }
    }

    private sealed class RecordingWriteInterceptor(string name, List<string> order) : IWriteInterceptor
    {
        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            order.Add(name);
            next(ref context);
        }
    }
}

[InterceptorSubject]
public partial class SubtreeSensor
{
    public partial string? Name { get; set; }

    public partial int Value { get; set; }

    public partial SubtreeSensor? Child { get; set; }
}
