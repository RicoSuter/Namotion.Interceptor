using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Tests.Models;

namespace Namotion.Interceptor.Registry.Tests.GraphBehavior;

/// <summary>
/// Tests that parent/child references in the registry are fully cleaned up
/// when subjects detach, regardless of detach ordering.
/// </summary>
public class DetachCleanupTests
{
    // === Parent detaches before child (the root cause of the memory leak) ===

    [Fact]
    public void WhenParentDetachesBeforeChild_ThenChildHasNoStaleParentReferences()
    {
        // Arrange: root -> parent -> child
        // When root.Mother is set to null, the lifecycle processes:
        //   1. parent's IsContextDetach (removes parent from _knownSubjects)
        //   2. child's IsPropertyReferenceRemoved (lookup for parent fails)
        //   3. child's IsContextDetach
        // The parent-side cleanup in IsContextDetach ensures child's _parents
        // are cleared before the parent is removed from _knownSubjects.
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var registry = context.GetService<ISubjectRegistry>();

        var child = new Person { FirstName = "Child" };
        var parent = new Person { FirstName = "Parent", Mother = child };
        var root = new Person(context) { FirstName = "Root", Mother = parent };

        // Sanity: child has one parent reference
        Assert.Single(child.TryGetRegisteredSubject()!.Parents);

        // Act: detach parent (and transitively child)
        root.Mother = null;

        // Assert: both parent and child are removed from registry
        Assert.Single(registry.KnownSubjects); // only root remains
        Assert.DoesNotContain(parent, registry.KnownSubjects.Keys);
        Assert.DoesNotContain(child, registry.KnownSubjects.Keys);
    }

    [Fact]
    public void WhenParentDetaches_ThenParentPropertyChildrenListIsCleared()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var child = new Person { FirstName = "Child" };
        var parent = new Person { FirstName = "Parent", Mother = child };
        var root = new Person(context) { FirstName = "Root", Mother = parent };

        // Capture reference to parent's registered property before detach
        var parentMotherProperty = parent.TryGetRegisteredSubject()!
            .TryGetProperty(nameof(Person.Mother))!;

        // Sanity: parent's Mother property has one child
        Assert.Single(parentMotherProperty.Children);

        // Act
        root.Mother = null;

        // Assert: parent's property _children list is cleared
        Assert.Empty(parentMotherProperty.Children);
    }

    [Fact]
    public void WhenDeepHierarchyDetaches_ThenAllIntermediateReferencesAreCleared()
    {
        // Arrange: root -> A -> B -> C
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var registry = context.GetService<ISubjectRegistry>();

        var c = new Person { FirstName = "C" };
        var b = new Person { FirstName = "B", Mother = c };
        var a = new Person { FirstName = "A", Mother = b };
        var root = new Person(context) { FirstName = "Root", Mother = a };

        // Capture property references before detach
        var aMotherProp = a.TryGetRegisteredSubject()!.TryGetProperty(nameof(Person.Mother))!;
        var bMotherProp = b.TryGetRegisteredSubject()!.TryGetProperty(nameof(Person.Mother))!;

        // Sanity
        Assert.Single(b.TryGetRegisteredSubject()!.Parents);
        Assert.Single(c.TryGetRegisteredSubject()!.Parents);

        // Act: detach entire branch
        root.Mother = null;

        // Assert: registry only has root
        Assert.Single(registry.KnownSubjects);

        // Assert: all intermediate _children lists are cleared
        Assert.Empty(aMotherProp.Children);
        Assert.Empty(bMotherProp.Children);
    }

    // === Multi-parent child where one parent detaches (child stays alive) ===

    [Fact]
    public void WhenOneParentDetaches_ThenChildRetainsOnlyValidParentReference()
    {
        // Arrange: shared child is referenced by both Father.Mother and Mother.Mother
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var registry = context.GetService<ISubjectRegistry>();

        var shared = new Person { FirstName = "Shared" };
        var root = new Person(context)
        {
            FirstName = "Root",
            Father = new Person { FirstName = "Father", Mother = shared },
            Mother = new Person { FirstName = "Mother", Mother = shared }
        };

        // Sanity: shared has 2 parent references
        var sharedRegistered = shared.TryGetRegisteredSubject()!;
        Assert.Equal(2, sharedRegistered.Parents.Length);

        // Act: detach Father (which has a reference to shared)
        root.Father = null;

        // Assert: shared is still alive (referenced by Mother.Mother)
        Assert.Contains(shared, registry.KnownSubjects.Keys);

        // Assert: shared should have exactly 1 parent reference (from Mother.Mother only)
        Assert.Single(sharedRegistered.Parents);
        Assert.Equal(nameof(Person.Mother), sharedRegistered.Parents[0].Property.Name);
    }

    [Fact]
    public void WhenOneParentDetaches_ThenDetachedParentPropertyChildrenIsCleared()
    {
        // Arrange: shared child referenced by two parents
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var shared = new Person { FirstName = "Shared" };
        var father = new Person { FirstName = "Father", Mother = shared };
        var mother = new Person { FirstName = "Mother", Mother = shared };
        var root = new Person(context)
        {
            FirstName = "Root",
            Father = father,
            Mother = mother
        };

        // Capture Father's Mother property
        var fatherMotherProp = father.TryGetRegisteredSubject()!
            .TryGetProperty(nameof(Person.Mother))!;

        Assert.Single(fatherMotherProp.Children);

        // Act: detach Father
        root.Father = null;

        // Assert: Father's property _children is cleared
        Assert.Empty(fatherMotherProp.Children);
    }

    // === Collection-based parent/child cleanup ===

    [Fact]
    public void WhenCollectionParentDetaches_ThenChildrenListIsCleared()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var registry = context.GetService<ISubjectRegistry>();

        var child1 = new Person { FirstName = "Child1" };
        var child2 = new Person { FirstName = "Child2" };
        var collectionParent = new Person { FirstName = "CollectionParent", Children = [child1, child2] };
        var root = new Person(context) { FirstName = "Root", Mother = collectionParent };

        // Capture collection property
        var childrenProp = collectionParent.TryGetRegisteredSubject()!
            .TryGetProperty(nameof(Person.Children))!;

        Assert.Equal(2, childrenProp.Children.Length);

        // Act: detach collectionParent (and transitively child1, child2)
        root.Mother = null;

        // Assert
        Assert.Single(registry.KnownSubjects); // only root
        Assert.Empty(childrenProp.Children);
    }

    [Fact]
    public void WhenCollectionChildIsSharedAndOneParentDetaches_ThenChildRetainsValidParent()
    {
        // Arrange: child is in both a collection and a direct property
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var registry = context.GetService<ISubjectRegistry>();

        var shared = new Person { FirstName = "Shared" };
        var root = new Person(context)
        {
            FirstName = "Root",
            Mother = shared,
            Children = [shared]
        };

        // Sanity: shared has 2 parent references (Mother + Children[0])
        var sharedRegistered = shared.TryGetRegisteredSubject()!;
        Assert.Equal(2, sharedRegistered.Parents.Length);

        // Act: remove from collection, keep as Mother
        root.Children = [];

        // Assert: shared still alive, exactly 1 parent reference remaining
        Assert.Contains(shared, registry.KnownSubjects.Keys);
        Assert.Single(sharedRegistered.Parents);
        Assert.Equal(nameof(Person.Mother), sharedRegistered.Parents[0].Property.Name);
    }

    // === Reattach after detach ===

    [Fact]
    public void WhenSubjectIsDetachedAndReattached_ThenParentReferencesAreCorrect()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var child = new Person { FirstName = "Child" };
        var root = new Person(context) { FirstName = "Root", Mother = child };

        // Detach
        root.Mother = null;

        // Act: reattach
        root.Mother = child;

        // Assert: child has exactly one parent reference (fresh, not stale)
        var childRegistered = child.TryGetRegisteredSubject()!;
        Assert.Single(childRegistered.Parents);
        Assert.Equal(nameof(Person.Mother), childRegistered.Parents[0].Property.Name);

        // Assert: parent's property has exactly one child
        var motherProp = root.TryGetRegisteredSubject()!.TryGetProperty(nameof(Person.Mother))!;
        Assert.Single(motherProp.Children);
    }

    [Fact]
    public void WhenSubjectIsMovedToNewParent_ThenOldParentReferencesAreCleared()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var child = new Person { FirstName = "Child" };
        var root = new Person(context)
        {
            FirstName = "Root",
            Father = new Person { FirstName = "OldParent", Mother = child }
        };

        var oldParent = root.Father!;
        var oldParentMotherProp = oldParent.TryGetRegisteredSubject()!
            .TryGetProperty(nameof(Person.Mother))!;

        // Act: move child to root.Mother (child gets 2nd ref), then detach OldParent
        root.Mother = child;
        root.Father = null;

        // Assert: child is still in the registry (1 remaining ref from Root.Mother)
        var registry = context.GetService<ISubjectRegistry>();
        Assert.Contains(child, registry.KnownSubjects.Keys);

        // Assert: child has exactly one parent reference (Root.Mother)
        var childRegistered = registry.TryGetRegisteredSubject(child);
        Assert.NotNull(childRegistered);
        Assert.Single(childRegistered.Parents);
        Assert.Equal(nameof(Person.Mother), childRegistered.Parents[0].Property.Name);
        Assert.Same(root, childRegistered.Parents[0].Property.Parent.Subject);

        // Assert: old parent's property children are cleared
        Assert.Empty(oldParentMotherProp.Children);
    }
}
