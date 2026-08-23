using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Parent;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// Tests for the graph ownership model: occurrence-aware edges, exact-context authority,
/// provisional anchors, and reachability-based release including cycles.
/// </summary>
public class GraphOwnershipTests
{
    private static IInterceptorSubjectContext CreateContext()
    {
        return InterceptorSubjectContext
            .Create()
            .WithContextInheritance()
            .WithParents();
    }

    [Fact]
    public void WhenSubjectAppearsTwiceInCollection_ThenReferenceCountIsTwo()
    {
        // Arrange
        var context = CreateContext();
        var parent = new Person(context) { FirstName = "P" };
        var a = new Person { FirstName = "A" };
        var b = new Person { FirstName = "B" };

        // Act
        parent.Children = [a, a, b];

        // Assert
        Assert.Equal(2, a.GetReferenceCount());
        Assert.Equal(1, b.GetReferenceCount());
    }

    [Fact]
    public void WhenSubjectAppearsTwiceInCollection_ThenItHasTwoParentEntries()
    {
        // Arrange
        var context = CreateContext();
        var parent = new Person(context) { FirstName = "P" };
        var a = new Person { FirstName = "A" };

        // Act
        parent.Children = [a, a];

        // Assert
        var parents = a.GetParents();
        Assert.Equal(2, parents.Length);
        Assert.Contains(parents, p => Equals(p.Index, 0));
        Assert.Contains(parents, p => Equals(p.Index, 1));
    }

    [Fact]
    public void WhenDuplicateOccurrencesAreRemoved_ThenSubjectDetachesWithNoLeakedParentEntry()
    {
        // Arrange
        var context = CreateContext();
        var parent = new Person(context) { FirstName = "P" };
        var a = new Person { FirstName = "A" };
        var b = new Person { FirstName = "B" };
        parent.Children = [a, a, b];

        // Act
        parent.Children = [b];

        // Assert
        Assert.Equal(0, a.GetReferenceCount());
        Assert.Empty(a.GetParents());
        Assert.Null(a.TryGetContext());
        Assert.Equal(1, b.GetReferenceCount());
        Assert.Single(b.GetParents());
    }

    [Fact]
    public void WhenCollectionIsReordered_ThenParentIndicesAreRefreshed()
    {
        // Arrange
        var context = CreateContext();
        var parent = new Person(context) { FirstName = "P" };
        var a = new Person { FirstName = "A" };
        var b = new Person { FirstName = "B" };
        parent.Children = [a, b];

        // Act
        parent.Children = [b, a];

        // Assert
        var parentsOfA = a.GetParents();
        var parentsOfB = b.GetParents();
        Assert.Single(parentsOfA);
        Assert.Single(parentsOfB);
        Assert.Equal(1, parentsOfA[0].Index);
        Assert.Equal(0, parentsOfB[0].Index);
    }

    [Fact]
    public void WhenReorderedDuplicatesShrink_ThenRemainingParentIndicesAreRefreshed()
    {
        // Arrange
        var context = CreateContext();
        var parent = new Person(context) { FirstName = "P" };
        var a = new Person { FirstName = "A" };
        var b = new Person { FirstName = "B" };
        parent.Children = [a, a, b];

        // Act
        parent.Children = [b, a];

        // Assert
        Assert.Equal(1, a.GetReferenceCount());
        var parentsOfA = a.GetParents();
        Assert.Single(parentsOfA);
        Assert.Equal(1, parentsOfA[0].Index);
        Assert.Equal(0, b.GetParents()[0].Index);
    }

    [Fact]
    public void WhenChildHoldsBackEdgeToConstructorRoot_ThenReplacingTheChildKeepsTheRoot()
    {
        // Arrange: child.Mother = root is a back-edge; it must not consume the root's
        // provisional anchor, or replacing the child would release the whole tree.
        var context = CreateContext();
        var root = new Person(context) { FirstName = "R" };
        var child = new Person { FirstName = "C" };
        root.Father = child;
        child.Mother = root;

        // Act
        root.Father = new Person { FirstName = "N" };

        // Assert
        Assert.Same(context, root.TryGetContext());
        Assert.Equal(0, child.GetReferenceCount());
        Assert.Null(child.TryGetContext());
    }

    [Fact]
    public void WhenOrphanedSelfCycle_ThenSubjectIsReleased()
    {
        // Arrange
        var context = CreateContext();
        var parent = new Person(context) { FirstName = "P" };
        var child = new Person { FirstName = "C" };
        parent.Father = child;
        child.Father = child;

        // Act
        parent.Father = null;

        // Assert
        Assert.Equal(0, child.GetReferenceCount());
        Assert.Null(child.TryGetContext());
    }

    [Fact]
    public void WhenInternalCycleIsOrphaned_ThenCycleIsReleased()
    {
        // Arrange: parent -> a -> b <-> c
        var context = CreateContext();
        var c = new Person { FirstName = "C" };
        var b = new Person { FirstName = "B", Mother = c };
        c.Mother = b;
        var a = new Person { FirstName = "A", Mother = b };
        var parent = new Person(context) { FirstName = "P", Father = a };

        var detached = new List<IInterceptorSubject>();
        context.TryGetLifecycleInterceptor()!.SubjectDetaching += change =>
        {
            if (change.IsContextDetach)
            {
                detached.Add(change.Subject);
            }
        };

        // Act
        parent.Father = null;

        // Assert: a, b and c are all released, including the b <-> c cycle
        Assert.Contains(a, detached);
        Assert.Contains(b, detached);
        Assert.Contains(c, detached);
        Assert.Equal(0, b.GetReferenceCount());
        Assert.Equal(0, c.GetReferenceCount());
        Assert.Null(b.TryGetContext());
        Assert.Null(c.TryGetContext());
    }

    /// <summary>parent1 -> b, b &lt;-&gt; c, parent2 -> c.</summary>
    private static (Person Parent1, Person Parent2, Person B, Person C) CreateCycleUnderTwoRoots(
        IInterceptorSubjectContext context)
    {
        var c = new Person { FirstName = "C" };
        var b = new Person { FirstName = "B", Mother = c };
        c.Mother = b;
        var parent1 = new Person(context) { FirstName = "P1", Father = b };
        var parent2 = new Person(context) { FirstName = "P2", Father = c };
        return (parent1, parent2, b, c);
    }

    [Fact]
    public void WhenCycleLosesOneOfTwoRoots_ThenItIsRetained()
    {
        // Arrange
        var context = CreateContext();
        var (parent1, _, b, c) = CreateCycleUnderTwoRoots(context);

        // Act: parent1 lets go, parent2 still reaches the cycle through c
        parent1.Father = null;

        // Assert
        Assert.NotNull(b.TryGetContext());
        Assert.NotNull(c.TryGetContext());
        Assert.Equal(1, b.GetReferenceCount());
        Assert.Equal(2, c.GetReferenceCount());
    }

    [Fact]
    public void WhenCycleLosesBothRoots_ThenItIsReleased()
    {
        // Arrange
        var context = CreateContext();
        var (parent1, parent2, b, c) = CreateCycleUnderTwoRoots(context);
        parent1.Father = null;

        // Act
        parent2.Father = null;

        // Assert
        Assert.Null(b.TryGetContext());
        Assert.Null(c.TryGetContext());
        Assert.Equal(0, b.GetReferenceCount());
        Assert.Equal(0, c.GetReferenceCount());
    }

    /// <summary>root -> left -> shared and root -> right -> shared.</summary>
    private static (Person Left, Person Right, Person Shared) CreateSharedDagChild(
        IInterceptorSubjectContext context)
    {
        var shared = new Person { FirstName = "S" };
        var left = new Person { FirstName = "L", Father = shared };
        var right = new Person { FirstName = "R", Father = shared };
        _ = new Person(context) { FirstName = "Root", Mother = left, Father = right };
        return (left, right, shared);
    }

    [Fact]
    public void WhenSharedDagChildLosesOneParent_ThenItIsRetained()
    {
        // Arrange
        var context = CreateContext();
        var (left, _, shared) = CreateSharedDagChild(context);

        // Act
        left.Father = null;

        // Assert
        Assert.Equal(1, shared.GetReferenceCount());
        Assert.NotNull(shared.TryGetContext());
    }

    [Fact]
    public void WhenSharedDagChildLosesBothParents_ThenItIsReleased()
    {
        // Arrange
        var context = CreateContext();
        var (left, right, shared) = CreateSharedDagChild(context);
        left.Father = null;

        // Act
        right.Father = null;

        // Assert
        Assert.Equal(0, shared.GetReferenceCount());
        Assert.Null(shared.TryGetContext());
    }

    [Fact]
    public void WhenSubtreeIsSharedByTwoRoots_ThenDetachingOneRootRetainsIt()
    {
        // Arrange
        var context = CreateContext();
        var shared = new Person { FirstName = "S" };
        var root1 = new Person(context) { FirstName = "R1", Father = shared };
        var root2 = new Person(context) { FirstName = "R2", Father = shared };
        Assert.Equal(2, shared.GetReferenceCount());

        // Act
        root1.Father = null;

        // Assert
        Assert.NotNull(shared.TryGetContext());
        Assert.Equal(1, shared.GetReferenceCount());
    }

    [Fact]
    public void WhenSubjectIsAttached_ThenTryGetContextReturnsTheExactContext()
    {
        // Arrange
        var context = CreateContext();
        var parent = new Person(context) { FirstName = "P" };
        var child = new Person { FirstName = "C" };

        // Act
        parent.Father = child;

        // Assert
        Assert.Same(context, parent.TryGetContext());
        Assert.Same(context, child.TryGetContext());
    }

    [Fact]
    public void WhenConstructorAttachedSubjectGainsAndLosesEdge_ThenItDetaches()
    {
        // Arrange: the constructor anchor is provisional and is consumed by the first edge
        var context = CreateContext();
        var parent = new Person(context) { FirstName = "P" };
        var child = new Person(context) { FirstName = "C" };
        Assert.Same(context, child.TryGetContext());

        // Act
        parent.Father = child;
        parent.Father = null;

        // Assert
        Assert.Null(child.TryGetContext());
        Assert.Equal(0, child.GetReferenceCount());
    }

    [Fact]
    public void WhenExplicitlyAttachedSubjectLosesItsEdge_ThenItStaysAttached()
    {
        // Arrange: an explicit anchor is never cleared by edge changes
        var context = CreateContext();
        var parent = new Person(context) { FirstName = "P" };
        var child = new Person { FirstName = "C" };
        child.AttachToContext(context);
        Assert.Same(context, child.TryGetContext());

        // Act
        parent.Father = child;
        parent.Father = null;

        // Assert
        Assert.Same(context, child.TryGetContext());
    }

    [Fact]
    public void WhenConstructorAttachedSubjectIsPromotedToExplicit_ThenItSurvivesEdgeRemoval()
    {
        // Arrange: promoting a subject that is already in the graph sets an explicit anchor without
        // repeating its attach callbacks, and the explicit anchor outlives the edge that adopted it
        // where the provisional one would not have.
        var context = CreateContext();
        var parent = new Person(context) { FirstName = "P" };
        var child = new Person(context) { FirstName = "C" };
        child.AttachToContext(context);

        // Act
        parent.Father = child;
        parent.Father = null;

        // Assert: the explicit anchor keeps the subject attached
        Assert.Same(context, child.TryGetContext());
        Assert.Equal(0, child.GetReferenceCount());
    }

    [Fact]
    public void WhenPromotedSubjectHoldsASubtree_ThenTheSubtreeIsRetained()
    {
        // Arrange: a subject promoted to an explicit root after it entered the graph keeps its
        // own children reachable even when an unrelated removal runs the reachability scan.
        var context = CreateContext();
        var parent = new Person(context) { FirstName = "P" };
        var promoted = new Person { FirstName = "X" };
        var held = new Person { FirstName = "H" };
        parent.Father = promoted;
        promoted.Father = held;
        promoted.AttachToContext(context);

        // Act: the promoted subject loses its edge from the parent; its explicit anchor must
        // keep both it and its child attached.
        parent.Father = null;

        // Assert
        Assert.Same(context, promoted.TryGetContext());
        Assert.Same(context, held.TryGetContext());
        Assert.Equal(1, held.GetReferenceCount());
    }

    [Fact]
    public void WhenSubjectFromAnotherContextIsAssigned_ThenWriteIsRejectedBeforeCommit()
    {
        // Arrange
        var context1 = CreateContext();
        var context2 = CreateContext();
        var parent = new Person(context1) { FirstName = "P" };
        var foreign = new Person(context2) { FirstName = "F" };

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => parent.Father = foreign);
        Assert.Null(parent.Father);
    }

    [Fact]
    public void WhenAddedOccurrenceCollidesWithRetainedIndex_ThenBothOccurrencesAreTracked()
    {
        // Arrange: after [b, a] the subject holds the stale index 1; the addition in [a, a] is
        // also attached at index 1, so the two must not be conflated into one occurrence.
        var referenceAddedEvents = new List<IInterceptorSubject>();
        var context = CreateContext()
            .WithService(() => new DelegateLifecycleHandler(change =>
            {
                if (change.IsPropertyReferenceAdded)
                {
                    referenceAddedEvents.Add(change.Subject);
                }
            }), _ => false);

        var parent = new Person(context) { FirstName = "P" };
        var a = new Person { FirstName = "A" };
        var b = new Person { FirstName = "B" };

        // Act
        parent.Children = [b, a];
        parent.Children = [a, a];

        // Assert
        Assert.Equal(2, a.GetReferenceCount());
        Assert.Equal(2, referenceAddedEvents.Count(subject => ReferenceEquals(subject, a)));
        var parents = a.GetParents();
        Assert.Equal(2, parents.Length);
        Assert.Contains(parents, p => Equals(p.Index, 0));
        Assert.Contains(parents, p => Equals(p.Index, 1));
    }

    [Fact]
    public void WhenCollidedDuplicateShrinksBackToOne_ThenSubjectStaysAttached()
    {
        // Arrange
        var context = CreateContext();
        var parent = new Person(context) { FirstName = "P" };
        var a = new Person { FirstName = "A" };
        var b = new Person { FirstName = "B" };
        parent.Children = [b, a];
        parent.Children = [a, a];

        // Act
        parent.Children = [a];

        // Assert: one live occurrence remains, so the subject must stay attached.
        Assert.Equal(1, a.GetReferenceCount());
        Assert.NotNull(a.TryGetContext());
        Assert.Single(a.GetParents());
    }

    [Fact]
    public void WhenAddedOccurrenceCollidesWithinTripleOccurrence_ThenAllOccurrencesAreTracked()
    {
        // Arrange: the subject already occupies indices 0 and 2; the addition lands on index 2.
        var context = CreateContext();
        var parent = new Person(context) { FirstName = "P" };
        var a = new Person { FirstName = "A" };
        var b = new Person { FirstName = "B" };
        parent.Children = [a, b, a];

        // Act
        parent.Children = [a, a, a];

        // Assert
        Assert.Equal(3, a.GetReferenceCount());
        var parents = a.GetParents();
        Assert.Equal(3, parents.Length);
        Assert.Contains(parents, p => Equals(p.Index, 0));
        Assert.Contains(parents, p => Equals(p.Index, 1));
        Assert.Contains(parents, p => Equals(p.Index, 2));
        Assert.Equal(0, b.GetReferenceCount());
        Assert.Null(b.TryGetContext());
    }

#if DEBUG
    [Fact]
    public void WhenLifecycleCallbackWritesStructuralProperty_ThenTheDebugGuardRejectsIt()
    {
        // Arrange: a handler reacting to b's removal writes another structural property of the
        // same graph. That used to release the writing parent mid-reconcile and relied on an
        // inexact incoming-edge fallback to drain edges whose stored index lagged the committed
        // value; both accommodations are deleted, and the write is a contract violation that the
        // debug guard rejects before its backing writer runs.
        var callbackObserved = false;
        Exception? callbackException = null;
        Person? root = null;
        Person? b = null;
        var context = CreateContext()
            .WithService(() => new DelegateLifecycleHandler(change =>
            {
                if (callbackObserved || !change.IsPropertyReferenceRemoved || !ReferenceEquals(change.Subject, b))
                {
                    return;
                }

                callbackObserved = true;
                callbackException = Record.Exception(() => root!.Father = null);
            }), _ => false);

        root = new Person(context) { FirstName = "R" };
        var parent = new Person { FirstName = "P" };
        b = new Person { FirstName = "B" };
        var a = new Person { FirstName = "A" };
        root.Father = parent;
        parent.Children = [b, a];

        // Act: the removal of b publishes the callback; the callback's write is rejected, so the
        // outer write itself completes normally.
        parent.Children = [a];

        // Assert
        Assert.True(callbackObserved);
        Assert.IsType<InvalidOperationException>(callbackException);
        Assert.Contains("lifecycle callback must not write a structural", callbackException.Message);
        Assert.Same(context, root.TryGetContext());
        Assert.Same(context, parent.TryGetContext());
        Assert.Equal(1, a.GetReferenceCount());
        Assert.Equal(0, b.GetReferenceCount());
        Assert.Null(b.TryGetContext());
    }
#endif

    /// <summary>Runs a delegate on every property detach, for tests that react from inside a
    /// property lifecycle callback, which is exempt from the callback write contract.</summary>
    private sealed class DelegatePropertyDetachHandler(Action<SubjectPropertyLifecycleChange> onDetach) : IPropertyLifecycleHandler
    {
        public void AttachProperty(SubjectPropertyLifecycleChange change)
        {
        }

        public void DetachProperty(SubjectPropertyLifecycleChange change)
        {
            onDetach(change);
        }
    }

    [Fact]
    public void WhenParentIsReleasedReentrantlyDuringCollectionWrite_ThenCommittedChildIsReleasedWithIt()
    {
        // Arrange: a callback reacting to b's release detaches the writing parent itself, so the
        // parent's release descends its already-committed new edges while a's stored incoming
        // edge still carries its pre-write index. The inexact-index removal in the ownership
        // state must still drain that edge, or a would stay attached to a released parent, and
        // the reconciler must stop publishing for the released parent. Routed through a property
        // lifecycle callback, which is exempt from the debug callback-write guard, so this
        // settled-graph behavior is exercised in Debug and Release alike; in Release a subject
        // lifecycle callback takes the same path because the guard compiles out.
        var released = false;
        Person? root = null;
        Person? b = null;
        var context = CreateContext()
            .WithService(() => new DelegatePropertyDetachHandler(change =>
            {
                if (!released && ReferenceEquals(change.Subject, b))
                {
                    released = true;
                    root!.Father = null;
                }
            }), _ => false);

        root = new Person(context) { FirstName = "R" };
        var parent = new Person { FirstName = "P" };
        b = new Person { FirstName = "B" };
        var a = new Person { FirstName = "A" };
        root.Father = parent;
        parent.Children = [b, a];

        // Act
        parent.Children = [a];

        // Assert: the whole former subtree is fully released.
        Assert.True(released);
        Assert.Null(parent.TryGetContext());
        Assert.Null(a.TryGetContext());
        Assert.Equal(0, a.GetReferenceCount());
        Assert.Empty(a.GetParents());
        Assert.Null(b.TryGetContext());
    }

#if !DEBUG
    [Fact]
    public void WhenSubjectCallbackWritesStructuralPropertyInRelease_ThenCommittedChildIsReleasedWithIt()
    {
        // Arrange: the original arrangement of the settled-graph test above, through a subject
        // lifecycle callback. In Debug that write is rejected by the callback guard (see the
        // Debug-only contract test); in Release the guard compiles out, so the write must take
        // the same accommodated path as the property-callback variant and settle identically.
        var released = false;
        Person? root = null;
        Person? b = null;
        var context = CreateContext()
            .WithService(() => new DelegateLifecycleHandler(change =>
            {
                if (!released && change.IsContextDetach && ReferenceEquals(change.Subject, b))
                {
                    released = true;
                    root!.Father = null;
                }
            }), _ => false);

        root = new Person(context) { FirstName = "R" };
        var parent = new Person { FirstName = "P" };
        b = new Person { FirstName = "B" };
        var a = new Person { FirstName = "A" };
        root.Father = parent;
        parent.Children = [b, a];

        // Act
        parent.Children = [a];

        // Assert: the whole former subtree is fully released.
        Assert.True(released);
        Assert.Null(parent.TryGetContext());
        Assert.Null(a.TryGetContext());
        Assert.Equal(0, a.GetReferenceCount());
        Assert.Empty(a.GetParents());
        Assert.Null(b.TryGetContext());
    }
#endif

    [Fact]
    public void WhenLifecycleCallbackWritesScalarProperty_ThenTheWriteIsAllowed()
    {
        // Arrange: scalar writes from callbacks stay supported; only structural writes are the
        // contract violation.
        Person? root = null;
        var context = CreateContext()
            .WithService(() => new DelegateLifecycleHandler(change =>
            {
                // The root's own construction-time attach fires before the local is assigned.
                if (change.IsContextAttach && root is not null)
                {
                    root.LastName = "attached";
                }
            }), _ => false);

        root = new Person(context) { FirstName = "R" };

        // Act
        root.Father = new Person { FirstName = "F" };

        // Assert
        Assert.Equal("attached", root.LastName);
    }

    [Fact]
    public void WhenRootIsDetachedWhileStillReferenced_ThenItStaysAttachedThroughTheEdge()
    {
        // Arrange: an explicitly attached subject that is also referenced by a live parent
        var context = CreateContext();
        var parent = new Person(context) { FirstName = "P" };
        var child = new Person { FirstName = "C" };
        child.AttachToContext(context);
        parent.Father = child;

        // Act: remove the explicit anchor; the structural edge still holds the subject
        child.DetachFromContext(context);

        // Assert
        Assert.Same(context, child.TryGetContext());
        Assert.Equal(1, child.GetReferenceCount());
    }

    [Fact]
    public void WhenDetachedRootLosesItsLastEdge_ThenItIsReleased()
    {
        // Arrange
        var context = CreateContext();
        var parent = new Person(context) { FirstName = "P" };
        var child = new Person { FirstName = "C" };
        child.AttachToContext(context);
        parent.Father = child;
        child.DetachFromContext(context);

        // Act
        parent.Father = null;

        // Assert
        Assert.Null(child.TryGetContext());
    }
}
