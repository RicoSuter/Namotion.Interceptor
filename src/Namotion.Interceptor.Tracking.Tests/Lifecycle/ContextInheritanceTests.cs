using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

public class ContextInheritanceTests
{
    [Fact]
    public void WhenTheParentASubjectInheritsFromLeavesWhileAnotherParentKeepsIt_ThenItStillResolvesServices()
    {
        // Arrange: the child inherits from the parent it was first attached to, then gains a second parent
        var root = new Person(InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry()) { FirstName = "Root" };
        var parent = new Person { FirstName = "Parent" };
        var child = new Person { FirstName = "Child" };
        root.Mother = parent;
        parent.Mother = child;
        root.Father = child;

        // Act: the first parent lets go of the child and then leaves the graph
        parent.Mother = null;
        root.Mother = null;

        // Assert
        Assert.NotNull(child.TryGetRegisteredSubject());
        Assert.Same(root, child.TryGetRegisteredSubject()!.Parents.Single().Property.Parent.Subject);
    }

    [Fact]
    public void WhenTheParentASubjectLeftReentersBelowIt_ThenTheirContextsFormNoCycle()
    {
        // Arrange
        var root = new Person(InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry()) { FirstName = "Root" };
        var parent = new Person { FirstName = "Parent" };
        var child = new Person { FirstName = "Child" };
        root.Mother = parent;
        parent.Mother = child;
        root.Father = child;
        parent.Mother = null;
        root.Mother = null;

        // Act: the former parent is attached below the child it held
        child.Mother = parent;

        // Assert: both still resolve the graph's services, which a delegation cycle would make throw
        Assert.NotNull(parent.TryGetRegisteredSubject());
        Assert.NotNull(child.TryGetRegisteredSubject());
        parent.FirstName = "Changed";
        Assert.Equal("Changed", parent.FirstName);
    }

    [Fact]
    public void WhenOnlyADescendantStillReferencesASubject_ThenItInheritsFromTheRootInsteadOfFormingACycle()
    {
        // Arrange: the child refers back to its parent, so the parent keeps a reference once its own parent drops it
        var root = new Person(InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry()) { FirstName = "Root" };
        var grandparent = new Person { FirstName = "Grandparent" };
        var parent = new Person { FirstName = "Parent" };
        var child = new Person { FirstName = "Child" };
        root.Mother = grandparent;
        grandparent.Mother = parent;
        parent.Mother = child;
        child.Father = parent;

        // Act: the grandparent lets go of the parent and then leaves the graph, before the parent returns
        grandparent.Mother = null;
        root.Mother = null;
        root.Father = parent;

        // Assert
        Assert.NotNull(parent.TryGetRegisteredSubject());
        Assert.NotNull(child.TryGetRegisteredSubject());
        child.FirstName = "Changed";
        Assert.Equal("Changed", child.FirstName);
    }
}
