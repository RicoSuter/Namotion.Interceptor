using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// Issue #207, both reproductions. On master the reverse registration set of the root context grows
/// by one per cycle, which is the 8,558 entries the issue measured. The two paths diverge before
/// they converge, so each gets its own test: the first has a constructor and parent context
/// mismatch, the second has none and leaks purely through multi-parent removal order.
/// </summary>
public class AttachEdgeLeakTests
{
    [Fact]
    public void WhenConstructorAttachedChildIsAddedAndRemovedRepeatedly_ThenTheRootContextDoesNotAccumulateEntries()
    {
        // Arrange
        var rootContext = InterceptorSubjectContext
            .Create()
            .WithContextInheritance();

        var parent = new Person(rootContext) { FirstName = "Parent" };
        var baseline = UsedByContextsProbe.Count(rootContext);

        // Act
        for (var cycle = 0; cycle < 3; cycle++)
        {
            var child = new Person(rootContext) { FirstName = "Child" };
            parent.Children = [child];
            parent.Children = [];
        }

        // Assert
        Assert.Equal(baseline, UsedByContextsProbe.Count(rootContext));
    }

    [Fact]
    public void WhenSharedChildIsRemovedFromItsParentsInOrder_ThenTheFirstParentContextDoesNotAccumulateEntries()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithContextInheritance();

        var parent1 = new Person(context) { FirstName = "P1" };
        var parent2 = new Person(context) { FirstName = "P2" };
        var parent1Context = ((IInterceptorSubject)parent1).Context;
        var baseline = UsedByContextsProbe.Count(parent1Context);

        // Act
        for (var cycle = 0; cycle < 3; cycle++)
        {
            var child = new Person { FirstName = "Child" };
            parent1.Children = [child];
            parent2.Children = [child];
            parent1.Children = [];
            parent2.Children = [];
        }

        // Assert
        Assert.Equal(baseline, UsedByContextsProbe.Count(parent1Context));
    }

    [Fact]
    public void WhenConstructorAttachedChildIsFullyDetached_ThenItStopsResolvingInterceptors()
    {
        // Arrange
        var rootContext = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        var parent = new Person(rootContext) { FirstName = "Parent" };
        var child = new Person(rootContext) { FirstName = "Child" };

        // Act
        parent.Children = [child];
        parent.Children = [];

        // Assert
        Assert.Empty(((IInterceptorSubject)child).Context.GetServices<IWriteInterceptor>());
    }
}
