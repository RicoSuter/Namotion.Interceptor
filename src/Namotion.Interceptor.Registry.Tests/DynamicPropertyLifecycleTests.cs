using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Tests.Models;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Registry.Tests;

/// <summary>
/// Tests that dynamic properties added via AddProperty/AddDerivedProperty
/// are correctly tracked by the lifecycle interceptor and registry.
/// </summary>
public class DynamicPropertyLifecycleTests
{
    [Fact]
    public void WhenDynamicDerivedPropertyReturnsSubject_ThenSubjectIsTrackedInRegistry()
    {
        // Arrange: Dynamic derived property that returns a subject reference (e.g., computed "first child").
        // Verifies that lifecycle tracking correctly attaches/detaches subjects discovered
        // through dynamic derived properties (IsDerived=true, IsIntercepted=true).
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var child1 = new Person { FirstName = "Child1" };
        var child2 = new Person { FirstName = "Child2" };

        var root = new Person(context)
        {
            FirstName = "Root",
            Children = [child1, child2]
        };

        var registry = context.GetService<ISubjectRegistry>();
        var registeredRoot = root.TryGetRegisteredSubject()!;

        // Act: Add a dynamic derived property that returns the first child
        var derivedProperty = registeredRoot.AddDerivedProperty<Person>(
            "FirstChild",
            _ => root.Children.Length > 0 ? root.Children[0] : null);

        // Assert: child1 is now referenced from both Children (index 0) and FirstChild.
        // Ref count should be 2 (one from Children, one from FirstChild).
        Assert.Equal(2, child1.GetReferenceCount());
        Assert.Equal(1, child2.GetReferenceCount());

        // All subjects tracked: root + child1 + child2
        Assert.Equal(3, registry.KnownSubjects.Count);

        // Act: Change Children so that child2 is first — derived property should update
        root.Children = [child2];

        // Assert: child1 fully detached (removed from Children and no longer FirstChild)
        Assert.Equal(0, child1.GetReferenceCount());

        // child2 referenced from both Children and FirstChild
        Assert.Equal(2, child2.GetReferenceCount());

        // root + child2
        Assert.Equal(2, registry.KnownSubjects.Count);

        // Act: Set Children to empty — derived property returns null
        root.Children = [];

        // Assert: child2 fully detached
        Assert.Equal(0, child2.GetReferenceCount());

        // Only root remains
        Assert.Single(registry.KnownSubjects);
    }
}
