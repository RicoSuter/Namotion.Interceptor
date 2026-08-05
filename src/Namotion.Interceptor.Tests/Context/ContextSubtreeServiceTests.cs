using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Tests.Context;

/// <summary>
/// A subject may register services of its own, and they apply to that subject and everything below
/// it without reaching anything else in the graph. That is what lets one component bring its own
/// interceptors into a shared object graph without touching subjects it does not own, and it is the
/// reason a subject resolves through the context of its parent rather than straight through the
/// context the graph was created with.
///
/// It falls out of the resolution rules rather than being implemented anywhere: a subject that
/// registers a service stops delegating and becomes a context that answers, so its own services
/// come first and the services of the context it was attached to follow. Its children then resolve
/// through it, which is what scopes it to the subtree, and nothing outside reaches it because
/// resolution only ever walks toward the root.
/// </summary>
public class ContextSubtreeServiceTests
{
    [Fact]
    public void WhenSubjectRegistersOwnInterceptor_ThenItAppliesToItselfAndItsChildrenOnly()
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

        // Assert: the subject that registered it and its child are both intercepted.
        Assert.Equal(2, ownedInterceptor.WriteCount);

        // Act: a sibling subtree of the same root, which the interceptor must not reach.
        rootSensor.Child = foreignSensor;
        foreignSensor.Child = foreignChild;
        foreignSensor.Value = 3;
        foreignChild.Value = 4;

        // Assert
        Assert.Equal(2, ownedInterceptor.WriteCount);
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
    /// Registering a service turns a subject that only delegated into one that answers, so the
    /// resolution of everything below it has to notice.
    /// </summary>
    [Fact]
    public void WhenSubjectRegistersOwnInterceptorAfterItsChildrenResolved_ThenTheChildrenSeeIt()
    {
        // Arrange: the child only inherits the context of its parent when the graph tracks
        // attachments, which is what makes it resolve through the parent at all.
        var rootContext = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        var parent = new SubtreeSensor(rootContext);
        var child = new SubtreeSensor();
        parent.Child = child;

        // Resolves and caches the chain of both subjects while neither has services.
        child.Value = 1;

        var interceptor = new CountingWriteInterceptor();

        // Act
        ((IInterceptorSubject)parent).Context.AddService<IWriteInterceptor>(interceptor);
        child.Value = 2;

        // Assert
        Assert.Equal(1, interceptor.WriteCount);
    }

    /// <summary>
    /// Pins step 2 before step 3 of the resolution order documented in docs/interceptor.md: a
    /// fallback context composed onto a subject's own context resolves before its inherited parent
    /// link, so explicit composition beats inheritance.
    /// </summary>
    [Fact]
    public void WhenSubjectHasBothAComposedFallbackAndAParentLink_ThenTheComposedOneResolvesFirst()
    {
        // Arrange
        var order = new List<string>();

        var rootContext = InterceptorSubjectContext
            .Create()
            .WithContextInheritance();

        var inheritedInterceptor = new RecordingWriteInterceptor("inherited", order);
        rootContext.AddService<IWriteInterceptor>(inheritedInterceptor);

        var parent = new SubtreeSensor(rootContext);
        var child = new SubtreeSensor();

        // The parent link is published when the child becomes a property child, so it is in place
        // before the fallback is composed onto the child's own context.
        parent.Child = child;

        // Carries no lifecycle interceptor, because adding a lifecycle-bearing context to a
        // subject's own context is rejected.
        var composedContext = InterceptorSubjectContext.Create();
        var composedInterceptor = new RecordingWriteInterceptor("composed", order);
        composedContext.AddService<IWriteInterceptor>(composedInterceptor);

        var childContext = ((IInterceptorSubject)child).Context;
        childContext.AddFallbackContext(composedContext);

        order.Clear();

        // Act
        child.Value = 1;

        // Assert
        Assert.Equal(["composed", "inherited"], order);

        var resolved = childContext
            .GetServices<IWriteInterceptor>()
            .OfType<RecordingWriteInterceptor>()
            .ToArray();

        Assert.Equal([composedInterceptor, inheritedInterceptor], resolved);
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

        public override string ToString() => name;
    }
}

[InterceptorSubject]
public partial class SubtreeSensor
{
    public partial string? Name { get; set; }

    public partial int Value { get; set; }

    public partial SubtreeSensor? Child { get; set; }
}
