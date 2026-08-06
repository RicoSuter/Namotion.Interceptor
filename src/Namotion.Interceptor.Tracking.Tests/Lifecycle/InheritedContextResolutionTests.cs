using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// The audited external consumer's one hard requirement: a child's and a grandchild's own context
/// must resolve the graph's services, because a source constructor does
/// <c>subject.Context.TryGetLifecycleInterceptor() ?? throw</c>. Must pass against unmodified master.
/// </summary>
public class InheritedContextResolutionTests
{
    [Fact]
    public void WhenGraphHasSettled_ThenChildAndGrandchildContextsResolveTheGraphServices()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithContextInheritance();

        var root = new Person(context) { FirstName = "Root" };
        var grandchild = new Person { FirstName = "Grandchild" };
        var child = new Person { FirstName = "Child", Mother = grandchild };

        // Act
        root.Mother = child;

        // Assert
        Assert.NotNull(((IInterceptorSubject)child).Context.TryGetService<ISubjectRegistry>());
        Assert.NotNull(((IInterceptorSubject)child).Context.TryGetLifecycleInterceptor());
        Assert.NotNull(((IInterceptorSubject)grandchild).Context.TryGetService<ISubjectRegistry>());
        Assert.NotNull(((IInterceptorSubject)grandchild).Context.TryGetLifecycleInterceptor());
    }

    [Fact]
    public void WhenSubjectIsSeededLikeAConnectorItem_ThenItIsRegistryVisibleBeforeAssignment()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithContextInheritance();

        var parent = new Person(context) { FirstName = "Parent" };
        var item = new Person { FirstName = "Item" };

        // Act
        ((IInterceptorSubject)item).AttachToContext(((IInterceptorSubject)parent).Context);
        var registeredBeforeAssignment = ((IInterceptorSubject)item).TryGetRegisteredSubject();

        parent.Mother = item;

        // Assert
        Assert.NotNull(registeredBeforeAssignment);
        Assert.NotNull(((IInterceptorSubject)item).TryGetRegisteredSubject());
        Assert.Equal(1, item.GetReferenceCount());
    }
}
