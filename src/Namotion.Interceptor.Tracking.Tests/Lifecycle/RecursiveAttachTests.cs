using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// Tests that verify recursive discovery of descendants when subjects enter the graph.
/// Discovery happens through ContextInheritanceHandler → AttachSubjectToContext, which
/// seeds _lastProcessedValues and recursively attaches children.
///
/// WithLifecycle() alone does NOT discover grandchildren — subjects must have the
/// context (via inheritance or manual assignment) for the lifecycle interceptor to
/// observe their properties.
/// </summary>
public class RecursiveAttachTests
{
    // ──────────────────────────────────────────────
    // Context inheritance discovers all descendants
    // ──────────────────────────────────────────────

    [Fact]
    public void WhenChildWithPrePopulatedGrandchildIsAttached_ThenGrandchildIsAlsoAttached()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithContextInheritance();

        var lifecycleInterceptor = context.TryGetLifecycleInterceptor()!;
        var attached = new List<IInterceptorSubject>();
        lifecycleInterceptor.SubjectAttached += change => attached.Add(change.Subject);

        var parent = new Person(context) { FirstName = "Parent" };

        var grandmother = new Person { FirstName = "Grandmother" };
        var mother = new Person { FirstName = "Mother", Mother = grandmother };

        // Act
        parent.Mother = mother;

        // Assert — both Mother and Grandmother must be attached
        Assert.Contains(mother, attached);
        Assert.Contains(grandmother, attached);
    }

    [Fact]
    public void WhenChildWithPrePopulatedCollectionIsAttached_ThenCollectionChildrenAreAlsoAttached()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithContextInheritance();

        var lifecycleInterceptor = context.TryGetLifecycleInterceptor()!;
        var attached = new List<IInterceptorSubject>();
        lifecycleInterceptor.SubjectAttached += change => attached.Add(change.Subject);

        var parent = new Person(context) { FirstName = "Parent" };

        var grandchild1 = new Person { FirstName = "Grandchild1" };
        var grandchild2 = new Person { FirstName = "Grandchild2" };
        var mother = new Person { FirstName = "Mother", Children = [grandchild1, grandchild2] };

        // Act
        parent.Mother = mother;

        // Assert
        Assert.Contains(grandchild1, attached);
        Assert.Contains(grandchild2, attached);
    }

    [Fact]
    public void WhenDeepHierarchyIsAttached_ThenAllLevelsAreAttached()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithContextInheritance();

        var lifecycleInterceptor = context.TryGetLifecycleInterceptor()!;
        var attached = new List<IInterceptorSubject>();
        lifecycleInterceptor.SubjectAttached += change => attached.Add(change.Subject);

        var parent = new Person(context) { FirstName = "Parent" };

        var greatGrandmother = new Person { FirstName = "GreatGrandmother" };
        var grandmother = new Person { FirstName = "Grandmother", Mother = greatGrandmother };
        var mother = new Person { FirstName = "Mother", Mother = grandmother };

        // Act
        parent.Mother = mother;

        // Assert — all three levels must be attached
        Assert.Contains(mother, attached);
        Assert.Contains(grandmother, attached);
        Assert.Contains(greatGrandmother, attached);
    }

    // ──────────────────────────────────────────────
    // Detach cascade cleans up discovered hierarchy
    // ──────────────────────────────────────────────

    [Fact]
    public void WhenParentOfDiscoveredHierarchyIsDetached_ThenAllLevelsAreDetached()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithContextInheritance();

        var lifecycleInterceptor = context.TryGetLifecycleInterceptor()!;

        var parent = new Person(context) { FirstName = "Parent" };

        var grandmother = new Person { FirstName = "Grandmother" };
        var mother = new Person { FirstName = "Mother", Mother = grandmother };
        parent.Mother = mother;

        var detached = new List<IInterceptorSubject>();
        lifecycleInterceptor.SubjectDetaching += change =>
        {
            if (change.IsContextDetach)
                detached.Add(change.Subject);
        };

        // Act
        parent.Mother = null;

        // Assert — both Mother and Grandmother must be detached
        Assert.Contains(mother, detached);
        Assert.Contains(grandmother, detached);
    }

    [Fact]
    public void WhenCollectionIsCleared_ThenDiscoveredGrandchildrenAreDetached()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithContextInheritance();

        var lifecycleInterceptor = context.TryGetLifecycleInterceptor()!;

        var parent = new Person(context) { FirstName = "Parent" };

        var grandchild = new Person { FirstName = "Grandchild" };
        var child = new Person { FirstName = "Child", Children = [grandchild] };
        parent.Children = [child];

        var detached = new List<IInterceptorSubject>();
        lifecycleInterceptor.SubjectDetaching += change =>
        {
            if (change.IsContextDetach)
                detached.Add(change.Subject);
        };

        // Act
        parent.Children = [];

        // Assert
        Assert.Contains(child, detached);
        Assert.Contains(grandchild, detached);
    }

    // ──────────────────────────────────────────────
    // Subsequent writes diff correctly against seeded baseline
    // ──────────────────────────────────────────────

    [Fact]
    public void WhenGrandchildIsReplacedAfterDiscovery_ThenOldIsDetachedAndNewIsAttached()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithContextInheritance();

        var lifecycleInterceptor = context.TryGetLifecycleInterceptor()!;

        var parent = new Person(context) { FirstName = "Parent" };

        var grandmother = new Person { FirstName = "Grandmother" };
        var mother = new Person { FirstName = "Mother", Mother = grandmother };
        parent.Mother = mother;

        var attached = new List<IInterceptorSubject>();
        var detached = new List<IInterceptorSubject>();
        lifecycleInterceptor.SubjectAttached += change => attached.Add(change.Subject);
        lifecycleInterceptor.SubjectDetaching += change =>
        {
            if (change.IsContextDetach)
                detached.Add(change.Subject);
        };

        // Act — replace grandmother with a new one
        var newGrandmother = new Person { FirstName = "NewGrandmother" };
        mother.Mother = newGrandmother;

        // Assert
        Assert.Contains(grandmother, detached);
        Assert.Contains(newGrandmother, attached);
    }

    // ──────────────────────────────────────────────
    // Cycles must not cause infinite recursion
    // ──────────────────────────────────────────────

    [Fact]
    public void WhenCyclicGraphIsAttached_ThenAllSubjectsAreAttachedWithoutInfiniteRecursion()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithContextInheritance();

        var lifecycleInterceptor = context.TryGetLifecycleInterceptor()!;
        var attached = new List<IInterceptorSubject>();
        lifecycleInterceptor.SubjectAttached += change => attached.Add(change.Subject);

        var parent = new Person(context) { FirstName = "Parent" };

        // Create cycle: A.Mother = B, B.Mother = A
        var nodeA = new Person { FirstName = "NodeA" };
        var nodeB = new Person { FirstName = "NodeB" };
        nodeA.Mother = nodeB;
        nodeB.Mother = nodeA;

        // Act — attach the cycle
        parent.Mother = nodeA;

        // Assert — both nodes attached, no stack overflow
        Assert.Contains(nodeA, attached);
        Assert.Contains(nodeB, attached);
    }

    // ──────────────────────────────────────────────
    // WithLifecycle() alone: only direct children tracked
    // ──────────────────────────────────────────────

    [Fact]
    public void WhenUsingLifecycleWithoutContextInheritance_ThenOnlyDirectChildrenAreAttached()
    {
        // Arrange — WithLifecycle() alone, no context inheritance
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();

        var lifecycleInterceptor = context.TryGetLifecycleInterceptor()!;
        var attached = new List<IInterceptorSubject>();
        lifecycleInterceptor.SubjectAttached += change => attached.Add(change.Subject);

        var parent = new Person(context) { FirstName = "Parent" };

        var grandmother = new Person { FirstName = "Grandmother" };
        var mother = new Person { FirstName = "Mother", Mother = grandmother };

        // Act
        parent.Mother = mother;

        // Assert — only direct child is attached, grandchild is NOT
        // (the recursive descent is driven by the context-inheritance handler, which is not registered)
        Assert.Contains(mother, attached);
        Assert.DoesNotContain(grandmother, attached);
    }

    // ──────────────────────────────────────────────
    // Null baseline: re-attached child without re-seeding
    // ──────────────────────────────────────────────

    [Fact]
    public void WhenReattachedChildWritesSameValue_ThenGrandchildIsRediscovered()
    {
        // Arrange — WithLifecycle() only (no context inheritance, no equality check).
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();

        var lifecycleInterceptor = context.TryGetLifecycleInterceptor()!;

        var parent = new Person(context) { FirstName = "Parent" };
        var child = new Person(context) { FirstName = "Child" };
        var grandmother = new Person(context) { FirstName = "Grandmother" };

        // Build hierarchy: parent → child → grandmother
        child.Mother = grandmother;
        parent.Mother = child;

        // Release the child, which takes its baselines and the grandmother with it.
        parent.Mother = null;
        Assert.Null(child.TryGetContext());
        Assert.Null(grandmother.TryGetContext());

        // Re-attach the child. There is no context-inheritance handler here, so nothing re-seeds
        // the child's own properties: its backing store still holds the grandmother, but the
        // lifecycle holds no baseline for child.Mother and the grandmother is not tracked.
        parent.Mother = child;
        Assert.Same(context, child.TryGetContext());
        Assert.Null(grandmother.TryGetContext());

        var attached = new List<IInterceptorSubject>();
        lifecycleInterceptor.SubjectAttached += change => attached.Add(change.Subject);

        // Act — re-write the same value to child.Mother (no equality interceptor to block it).
        // The reconcile finds no committed baseline for the property, and the missing baseline has
        // to read as null rather than as the property's current value: reading the current value
        // would make old and new reference-equal, return early, and leave the grandmother detached.
        child.Mother = grandmother;

        // Assert
        Assert.Contains(grandmother, attached);
        Assert.Same(context, grandmother.TryGetContext());
        Assert.Equal(1, grandmother.GetReferenceCount());
    }
}
