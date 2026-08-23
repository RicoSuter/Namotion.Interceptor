using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Parent;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// Composing two contexts that each configure Tracking stops working, because a subject belongs to
/// exactly one context's graph and both lifecycles would claim it. This is a declared breaking
/// change of exact-context ownership, not an accident of configuration order, so both ways it
/// surfaces are pinned here.
/// </summary>
/// <remarks>
/// It reaches further than <c>WithLifecycle()</c> suggests: <c>WithFullPropertyTracking()</c>,
/// <c>WithRegistry()</c>, <c>WithContextInheritance()</c>, <c>WithParents()</c> and
/// <c>WithSourceMonitoring()</c> all register a lifecycle. A context that only needs to contribute
/// interceptors or services to an aggregate must register those alone.
/// </remarks>
public class AggregatedTrackingContextTests
{
    [Fact]
    public void WhenTwoAggregatedContextsBothConfigureTracking_ThenConstructionThrows()
    {
        // Arrange
        var parent = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var child = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        child.AddFallbackContext(parent);

        // Act & Assert: the second lifecycle finds the subject already claimed by the first.
        var exception = Assert.Throws<InvalidOperationException>(() => new Person(child));
        Assert.Contains("owned by a different context", exception.Message);
    }

    [Fact]
    public void WhenSourceMonitoringIsAggregatedOntoTracking_ThenConstructionThrows()
    {
        // Arrange: the non-obvious shape. WithSourceMonitoring() reaches a lifecycle through
        // WithParents(), so a context that looks like it only adds monitoring adds a second owner.
        var parent = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var child = InterceptorSubjectContext.Create().WithParents();
        child.AddFallbackContext(parent);

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(() => new Person(child));
        Assert.Contains("owned by a different context", exception.Message);
    }

    [Fact]
    public void WhenASecondLifecycleIsComposedAfterAttach_ThenParentQueriesThrow()
    {
        // Arrange: composing the second context after the subject is already attached gets past
        // construction, and the ownership queries are the next thing to fail, because resolving the
        // built-in lifecycle from the subject's exact context no longer has one answer.
        var parent = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var child = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var person = new Person(child) { FirstName = "P" };

        // Act
        child.AddFallbackContext(parent);

        // Assert
        Assert.Throws<InvalidOperationException>(() => ((IInterceptorSubject)person).GetParents());
        Assert.Throws<InvalidOperationException>(() => person.GetReferenceCount());
    }

    [Fact]
    public void WhenOnlyOneAggregatedContextConfiguresTracking_ThenTheSubjectAttachesNormally()
    {
        // Arrange: the supported shape. The aggregated context contributes interceptors and
        // services; only one context owns the graph.
        var parent = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var child = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions();
        child.AddFallbackContext(parent);

        // Act
        var person = new Person(child) { FirstName = "P" };
        person.Father = new Person { FirstName = "F" };

        // Assert
        Assert.Same(parent, ((IInterceptorSubject)person).TryGetContext());
        Assert.Equal(1, person.Father.GetReferenceCount());
    }
}
