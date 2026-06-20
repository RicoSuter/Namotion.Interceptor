using Namotion.Interceptor.Registry.Tests.Models;
using Namotion.Interceptor.Testing;

namespace Namotion.Interceptor.Registry.Tests.GraphBehavior;

/// <summary>
/// Tests for Directed Acyclic Graph (DAG) scenarios where subjects can have multiple parents.
/// </summary>
public class DagTests
{
    [Fact]
    public Task WhenSharedNodeHasMultipleParents_ThenAllReferencesTracked()
    {
        // Arrange
        var helper = new TestLifecycleHandler();
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithService(() => helper);

        // Diamond pattern:
        // Root
        //   ├── A ──┐
        //   └── B ──┴── C (shared)

        var shared = new Person { FirstName = "Shared" };
        var nodeA = new Person
        {
            FirstName = "A",
            Mother = shared
        };
        var nodeB = new Person
        {
            FirstName = "B",
            Mother = shared
        };

        // Act
        var root = new Person(context)
        {
            FirstName = "Root",
            Father = nodeA,
            Mother = nodeB
        };

        // Assert - Shared should have refs: 2 (from A and B)
        return Verify(helper.GetEvents());
    }

    [Fact]
    public Task WhenRemovingOneParentOfSharedNode_ThenSharedNodeStays()
    {
        // Arrange
        var helper = new TestLifecycleHandler();
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithService(() => helper);

        var shared = new Person { FirstName = "Shared" };
        var nodeA = new Person
        {
            FirstName = "A",
            Mother = shared
        };
        var nodeB = new Person
        {
            FirstName = "B",
            Mother = shared
        };

        var root = new Person(context)
        {
            FirstName = "Root",
            Father = nodeA,
            Mother = nodeB
        };

        helper.Clear();

        // Act - Remove A (one parent of shared)
        root.Father = null;

        // Assert - A detaches, Shared stays (still referenced by B)
        return Verify(helper.GetEvents());
    }

    [Fact]
    public Task WhenRemovingAllParentsOfSharedNode_ThenSharedNodeDetaches()
    {
        // Arrange
        var helper = new TestLifecycleHandler();
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithService(() => helper);

        var shared = new Person { FirstName = "Shared" };
        var nodeA = new Person
        {
            FirstName = "A",
            Mother = shared
        };
        var nodeB = new Person
        {
            FirstName = "B",
            Mother = shared
        };

        var root = new Person(context)
        {
            FirstName = "Root",
            Father = nodeA,
            Mother = nodeB
        };

        root.Father = null; // Remove A first
        helper.Clear();

        // Act - Remove B (last parent of shared)
        root.Mother = null;

        // Assert - B detaches, Shared also detaches (no more references)
        return Verify(helper.GetEvents());
    }

    [Fact]
    public Task WhenReplacingSharedNodeReference_ThenCountUpdatesCorrectly()
    {
        // Arrange
        var helper = new TestLifecycleHandler();
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithService(() => helper);

        var shared = new Person { FirstName = "Shared" };
        var replacement = new Person { FirstName = "Replacement" };
        var nodeA = new Person
        {
            FirstName = "A",
            Mother = shared
        };
        var nodeB = new Person
        {
            FirstName = "B",
            Mother = shared
        };

        var root = new Person(context)
        {
            FirstName = "Root",
            Father = nodeA,
            Mother = nodeB
        };

        helper.Clear();

        // Act - Replace A's reference to shared with replacement
        nodeA.Mother = replacement;

        // Assert - Shared loses one ref (still has one from B), Replacement attached
        return Verify(helper.GetEvents());
    }

    [Fact]
    public void WhenCollectionIndexOfSecondParentShifts_ThenOverflowParentIndexIsUpdated()
    {
        // Arrange: Father occupies the shared child's inline first-parent slot, so the
        // Children entry is stored in the overflow parent list; its collection index
        // update must hit the overflow branch of UpdateParentIndex.
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var other = new Person { FirstName = "Other" };
        var shared = new Person { FirstName = "Shared" };
        var root = new Person(context)
        {
            FirstName = "Root",
            Father = shared,
            Children = [other, shared]
        };

        var sharedRegistered = shared.TryGetRegisteredSubject();
        Assert.NotNull(sharedRegistered);
        var childrenEntry = sharedRegistered.Parents.Single(p => p.Property.Name == nameof(Person.Children));
        Assert.Equal(1, (int)childrenEntry.Index!);

        // Act: removing the sibling shifts the shared child's collection index from 1 to 0.
        root.Children = [shared];

        // Assert
        childrenEntry = sharedRegistered.Parents.Single(p => p.Property.Name == nameof(Person.Children));
        Assert.Equal(0, (int)childrenEntry.Index!);
    }
}
