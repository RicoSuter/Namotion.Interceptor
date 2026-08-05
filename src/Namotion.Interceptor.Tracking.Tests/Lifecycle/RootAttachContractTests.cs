using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// Issues #402 defects 3, 4 and 5, the two-graph half-attach from spec section 2, and re-attach
/// during detach. Each must fail against unmodified master for the reason named in its comment.
/// Defect 2 (concurrent detach running the interceptors twice) and the remaining behaviour changes
/// need the new API, so they are the tests appended below.
/// </summary>
public class RootAttachContractTests
{
    [Fact]
    public void WhenAttachResolveThrows_ThenNoEdgeIsPublished()
    {
        // #402 defect 4: master publishes the edge and resolves after, so a failing resolve leaves
        // the edge registered with no attach callback having run.
        //
        // Only a PURE DELEGATION cycle raises. A context with two fallbacks has no delegation
        // target at all, so the ordinary service walk tolerates the loop through its visited set
        // and returns normally. Both loop contexts therefore carry no service and exactly one
        // fallback each.

        // Arrange
        var loopA = InterceptorSubjectContext.Create();
        var loopB = InterceptorSubjectContext.Create();
        loopA.AddFallbackContext(loopB);
        loopB.AddFallbackContext(loopA);

        var subject = new Person { FirstName = "Subject" };
        var baseline = UsedByContextsProbe.Count(loopA);

        // Act
        try
        {
            ((IInterceptorSubject)subject).AttachToContext(loopA);
        }
        catch (InvalidOperationException)
        {
            // Expected once the resolve precedes the publish.
        }

        // Assert
        Assert.Equal(baseline, UsedByContextsProbe.Count(loopA));
    }

    [Fact]
    public void WhenChainTurnedCyclicAfterAttach_ThenDetachStillRemovesTheEdge()
    {
        // #402 defects 3 and 5: master re-resolves at detach, so a chain that has since turned
        // cyclic raises before the edge is removed, and no other route can then remove it.
        //
        // The cycle has to be built by REWIRING an existing pure-delegation chain, because a
        // context cannot be given a second fallback without ceasing to be a pure delegator.

        // Arrange
        var graphContext = InterceptorSubjectContext
            .Create()
            .WithContextInheritance();

        var midContext = InterceptorSubjectContext.Create();
        midContext.AddFallbackContext(graphContext);

        var attachContext = InterceptorSubjectContext.Create();
        attachContext.AddFallbackContext(midContext);

        var subject = new Person { FirstName = "Subject" };
        ((IInterceptorSubject)subject).AttachToContext(attachContext);

        // attachContext -> midContext -> attachContext, both pure delegators, so resolving raises.
        midContext.RemoveFallbackContext(graphContext);
        midContext.AddFallbackContext(attachContext);

        // Counted after the rewiring, because closing the circle registers midContext into
        // attachContext's reverse set as well, and only the subject's own edge is expected to go.
        var attachedCount = UsedByContextsProbe.Count(attachContext);

        // Act: the detach raises either way, because the descent resolves handlers through the
        // subject's own now-cyclic chain. What this pins is that the edge comes out regardless.
        Assert.ThrowsAny<InvalidOperationException>(
            () => ((IInterceptorSubject)subject).DetachFromContext(attachContext));

        // Assert
        Assert.Equal(attachedCount - 1, UsedByContextsProbe.Count(attachContext));
    }

    [Fact]
    public void WhenSubjectOwnedByOneGraphIsAttachedToAnother_ThenItThrowsAndPublishesNothing()
    {
        // Spec section 2: on master both registries index the subject and only graph A resolves,
        // so graph B holds a subject it can enumerate and never hears from.

        // Arrange
        var contextA = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithContextInheritance();

        var contextB = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithContextInheritance();

        var parentA = new Person(contextA) { FirstName = "ParentA" };
        var shared = new Person { FirstName = "Shared" };
        parentA.Mother = shared;

        var parentB = new Person(contextB) { FirstName = "ParentB" };

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => parentB.Mother = shared);

        var registryB = contextB.TryGetService<ISubjectRegistry>()!;
        Assert.DoesNotContain(shared, registryB.KnownSubjects.Keys);
    }

    [Fact]
    public void WhenRootAttachedSubjectIsReferencedFromAnotherGraph_ThenItThrowsAndPublishesNothing()
    {
        // Spec section 9 lists TWO shapes for change 5 and this is the second: root in A, then
        // child in B. It is the shape that catches a root attach which records the attach context
        // without claiming ownership, because the parent-to-parent shape above claims ownership on
        // the property path and passes either way.

        // Arrange
        var contextA = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithContextInheritance();

        var contextB = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithContextInheritance();

        var subject = new Person { FirstName = "Subject" };
        ((IInterceptorSubject)subject).AttachToContext(contextA);

        var parentB = new Person(contextB) { FirstName = "ParentB" };

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => parentB.Mother = subject);

        var registryB = contextB.TryGetService<ISubjectRegistry>()!;
        Assert.DoesNotContain(subject, registryB.KnownSubjects.Keys);
        Assert.Equal(0, subject.GetReferenceCount());
    }


    [Fact]
    public void WhenHandlerReAttachesSubjectDuringItsOwnDetach_ThenItThrows()
    {
        // Behaviour change 17: under the redesign the same action would form an unrecoverable
        // parent-only cycle, so it fails fast. The handler swallows the throw so the outer detach
        // still completes, which is what lets the assertions run.

        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithContextInheritance();

        var parent = new Person(context) { FirstName = "Parent" };
        var reAttachTarget = new Person(context) { FirstName = "Target" };
        var caught = null as Exception;

        context.WithService(() => new ReAttachingHandler(reAttachTarget, exception => caught = exception));

        var child = new Person { FirstName = "Child" };
        parent.Mother = child;

        // Act
        parent.Mother = null;

        // Assert
        Assert.IsType<InvalidOperationException>(caught);
        Assert.Equal(0, child.GetReferenceCount());
    }

    [Fact]
    public async Task WhenTwoThreadsDetachTheSameRoot_ThenTheInterceptorsRunExactlyOnce()
    {
        // #402 defect 2. Nothing in the ILifecycleInterceptor contract requires a consumer's detach
        // to be idempotent, so running it twice is a defect even though Tracking's own happens to
        // tolerate it. TryClearAttachContext picks exactly one winner under the mutation lock.

        // Arrange
        var detachCount = 0;
        var context = InterceptorSubjectContext
            .Create()
            .WithService(() => new CountingLifecycleInterceptor(() => Interlocked.Increment(ref detachCount)), _ => false);

        var subject = new Person { FirstName = "Subject" };
        ((IInterceptorSubject)subject).AttachToContext(context);

        using var start = new ManualResetEventSlim(false);

        // Act
        var racers = Enumerable
            .Range(0, 2)
            .Select(_ => Task.Factory.StartNew(
                () =>
                {
                    start.Wait();
                    ((IInterceptorSubject)subject).DetachFromContext(context);
                },
                TaskCreationOptions.LongRunning))
            .ToArray();

        start.Set();
        await Task.WhenAll(racers);

        // Assert
        Assert.Equal(1, detachCount);
        Assert.False(((IInterceptorSubject)subject).IsAttached());
    }

    [Fact]
    public void WhenDetachInterceptorThrows_ThenTheEdgeIsStillRemovedAndAReattachWorks()
    {
        // Behaviour change 6.

        // Arrange
        var shouldThrow = true;
        var context = InterceptorSubjectContext
            .Create()
            .WithService(() => new CountingLifecycleInterceptor(() =>
            {
                if (shouldThrow)
                {
                    throw new InvalidOperationException("detach handler failed");
                }
            }), _ => false);

        var subject = new Person { FirstName = "Subject" };
        ((IInterceptorSubject)subject).AttachToContext(context);
        var attachedCount = UsedByContextsProbe.Count(context);

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => ((IInterceptorSubject)subject).DetachFromContext(context));

        // The edge assertion is the one that matters: the record is cleared before the interceptor
        // loop, so IsAttached() reports false even when the finally is deleted, and a re-attach
        // succeeds because AddFallbackContext dedups the leftover edge. Without this the mutant
        // that deletes the finally survives.
        Assert.Equal(attachedCount - 1, UsedByContextsProbe.Count(context));
        Assert.False(((IInterceptorSubject)subject).IsAttached());

        shouldThrow = false;
        ((IInterceptorSubject)subject).AttachToContext(context);
        Assert.True(((IInterceptorSubject)subject).IsAttached());
    }

    [Fact]
    public void WhenDetachHandlerThrowsOnTheRootPath_ThenOwnershipIsStillReleased()
    {
        // The root twin of the property path's release in a finally. DetachFromContext has already
        // cleared the record and its own finally removes the edge, so an ownership release skipped
        // by a throwing handler leaves a subject that belongs to no graph and still has an owner:
        // IsAttached() reports true forever and every later attach is rejected by both
        // TryRecordAttachContext and ClaimOwnership. The graphs carry WithContextInheritance()
        // because only Tracking's LifecycleInterceptor claims ownership in the first place, which is
        // why the change 6 test above does not cover this.

        // Arrange
        var graphA = InterceptorSubjectContext
            .Create()
            .WithContextInheritance();

        var graphB = InterceptorSubjectContext
            .Create()
            .WithContextInheritance();

        graphA.WithService(() => new ThrowingDetachHandler());

        var subject = new Person { FirstName = "Subject" };
        ((IInterceptorSubject)subject).AttachToContext(graphA);

        // Act
        var caught = Record.Exception(() => ((IInterceptorSubject)subject).DetachFromContext(graphA));

        // Assert
        Assert.IsType<InvalidOperationException>(caught);

        ((IInterceptorSubject)subject).AttachToContext(graphB);
        Assert.Same(graphB, ((IInterceptorSubject)subject).TryGetAttachContext());
    }

    [Fact]
    public void WhenDetachedRootIsAttachedToAnotherGraph_ThenOwnershipWasReleased()
    {
        // Kills the mutant that deletes the ownership release at the end of DetachRootSubject.
        // Without it a detached root stays owned by the graph it left, so IsAttached() keeps
        // reporting true and TryRecordAttachContext rejects the next attach into a different graph.
        // The graphs must carry WithContextInheritance(), because only Tracking's
        // LifecycleInterceptor claims ownership in the first place.

        // Arrange
        var graphA = InterceptorSubjectContext
            .Create()
            .WithContextInheritance();

        var graphB = InterceptorSubjectContext
            .Create()
            .WithContextInheritance();

        var subject = new Person { FirstName = "Subject" };
        ((IInterceptorSubject)subject).AttachToContext(graphA);

        // Act
        ((IInterceptorSubject)subject).DetachFromContext(graphA);
        var attachedAfterDetach = ((IInterceptorSubject)subject).IsAttached();
        ((IInterceptorSubject)subject).AttachToContext(graphB);

        // Assert
        Assert.False(attachedAfterDetach);
        Assert.True(((IInterceptorSubject)subject).IsAttached());
        Assert.Same(graphB, ((IInterceptorSubject)subject).TryGetAttachContext());
    }

    [Fact]
    public void WhenTwoInterceptorsAreCoResolved_ThenOwnershipIsStillReleasedOnFullDetach()
    {
        // Ownership is claimed by the FIRST interceptor to reach AttachToProperty and released by
        // whichever one observes count == 0, which is the LAST to decrement. With two co-resolved
        // interceptors those are never the same instance, so releasing on interceptor identity
        // leaves the subject owned forever: it reports IsAttached() with a reference count of zero
        // and can never join another graph, and no consumer call can clear it because
        // DetachFromContext finds no attach record.

        // Arrange
        var parentContext = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        var childContext = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        // After both are configured, so each keeps its own LifecycleInterceptor and the child
        // context resolves two of them.
        childContext.AddFallbackContext(parentContext);

        var parent = new Person(childContext) { FirstName = "Parent" };
        var child = new Person { FirstName = "Child" };

        // Act
        parent.Mother = child;
        parent.Mother = null;

        // Assert
        Assert.Equal(0, child.GetReferenceCount());
        Assert.False(((IInterceptorSubject)child).IsAttached());

        var otherGraph = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        ((IInterceptorSubject)child).AttachToContext(otherGraph);
        Assert.Same(otherGraph, ((IInterceptorSubject)child).TryGetAttachContext());
    }

    [Fact]
    public void WhenSubjectIsStillReferenced_ThenDetachFromContextThrowsAndLeavesTheCountIntact()
    {
        // Behaviour change 8.

        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithContextInheritance();

        var parent = new Person(context) { FirstName = "Parent" };
        var child = new Person { FirstName = "Child" };
        ((IInterceptorSubject)child).AttachToContext(context);
        parent.Mother = child;

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => ((IInterceptorSubject)child).DetachFromContext(context));

        Assert.Equal(1, child.GetReferenceCount());
        Assert.True(((IInterceptorSubject)child).IsAttached());
    }

    [Fact]
    public void WhenTwoEdgesTargetOneContext_ThenRemovingOneKeepsInvalidationReachingTheOther()
    {
        // Behaviour change 9, unreachable on master where one edge kind plus dedup rules it out.

        // Built on plain contexts through the internal setter, because the guard on
        // AddFallbackContext makes this shape unreachable from consumer code: adding a
        // lifecycle-bearing parent context to a child that has no attach record now throws. Two
        // edges to one target can therefore only arise inside the library, which is exactly why
        // the unregistration has to be conditional rather than why a consumer would hit it.

        // Arrange
        var target = InterceptorSubjectContext.Create();
        var user = InterceptorSubjectContext.Create();

        // An own service stops the user from delegating, so it resolves on its own state and fills
        // its own service cache. A pure delegator would answer through the target's cache instead
        // and a missed invalidation would leave nothing stale behind to observe.
        user.AddService(new OwnMarkerService());

        user.AddFallbackContext(target);
        Assert.True(user.TrySetParentContext(target));

        // Act: remove one of the two edges pointing at the same target.
        user.RemoveFallbackContext(target);

        // Assert
        Assert.True(user.HasParentContext);

        // Fills the user's cache for this service type, so the query after the registration below
        // can only see the service if the removal kept the reverse entry the parent link needs.
        Assert.Empty(user.GetServices<MarkerService>());

        target.AddService(new MarkerService());
        Assert.Single(user.GetServices<MarkerService>());
    }

    [Fact]
    public void WhenPreWiredChildIsAttachedUnderANewParent_ThenItsGrandchildIsDiscovered()
    {
        // Behaviour change 11: on master the descent is gated on AddFallbackContext's return value,
        // so pre-wiring a child's context suppresses discovery of everything below it.

        // The parent must NOT be attached yet. Pre-wiring to an already-attached parent attaches
        // the whole subtree at pre-wire time, so the later assignment exercises nothing and the
        // mutant that gates the descent on the reference count survives.

        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithContextInheritance();

        var parent = new Person { FirstName = "Parent" };
        var grandchild = new Person { FirstName = "Grandchild" };
        var child = new Person { FirstName = "Child", Mother = grandchild };

        // Legal: the parent carries no lifecycle interceptor yet, so the guard does not fire.
        ((IInterceptorSubject)child).Context.AddFallbackContext(((IInterceptorSubject)parent).Context);
        parent.Father = child;

        // Act
        ((IInterceptorSubject)parent).AttachToContext(context);

        // Assert
        Assert.NotNull(((IInterceptorSubject)grandchild).TryGetRegisteredSubject());
        Assert.Equal(1, grandchild.GetReferenceCount());
        Assert.NotEmpty(((IInterceptorSubject)grandchild).Context.GetServices<IWriteInterceptor>());
    }

    [Fact]
    public void WhenConstructorAttachedChildIsPlacedUnderAParent_ThenItInheritsTheParentsSubtreeServices()
    {
        // Behaviour change 12, the attach-side twin of change 10.

        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithContextInheritance();

        var parent = new Person(context) { FirstName = "Parent" };
        var child = new Person(context) { FirstName = "Child" };

        ((IInterceptorSubject)parent).Context.AddService(new MarkerService());

        // Act
        parent.Mother = child;

        // Assert
        Assert.Single(((IInterceptorSubject)child).Context.GetServices<MarkerService>());
    }

    [Fact]
    public void WhenReferenceCountsChange_ThenSubjectDataCarriesNoEntryForThem()
    {
        // Behaviour change 13.

        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithContextInheritance();

        var parent1 = new Person(context) { FirstName = "P1" };
        var parent2 = new Person(context) { FirstName = "P2" };
        var shared = new Person { FirstName = "Shared" };

        // Act
        parent1.Mother = shared;
        var afterFirst = shared.GetReferenceCount();

        parent2.Mother = shared;
        var afterSecond = shared.GetReferenceCount();

        parent1.Mother = null;
        var afterFirstRemoval = shared.GetReferenceCount();

        parent2.Mother = null;

        // Assert
        Assert.Equal(1, afterFirst);
        Assert.Equal(2, afterSecond);
        Assert.Equal(1, afterFirstRemoval);
        Assert.Equal(0, shared.GetReferenceCount());
        Assert.DoesNotContain(((IInterceptorSubject)shared).Data.Keys, key => key.key.Contains("ReferenceCount"));
    }

    [Fact]
    public void WhenSubjectUsesAPlainContext_ThenNoAttachContextIsRecorded()
    {
        // Decision 4. A context carrying no lifecycle interceptor is not a graph, so the
        // constructor's AttachToContext degenerates to plain composition and records nothing.
        // Spec section 7 already says such a subject reports IsAttached() false; recording would
        // have contradicted that.

        // Arrange
        var plainContext = InterceptorSubjectContext.Create();

        // Act
        var subject = new Person(plainContext) { FirstName = "Subject" };

        // Assert
        Assert.Null(((IInterceptorSubject)subject).TryGetAttachContext());
        Assert.False(((IInterceptorSubject)subject).IsAttached());
        Assert.Equal(0, subject.GetReferenceCount());
    }

    [Fact]
    public void WhenPlainContextSubjectJoinsAGraphLater_ThenItAttachesInOneStep()
    {
        // Decision 4's payoff, and the regression this guards: recording the plain context would
        // make this throw "already attached through a different context", and the only escape
        // would be a DetachFromContext nobody would guess at.

        // Arrange
        var plainContext = InterceptorSubjectContext.Create();
        var subject = new Person(plainContext) { FirstName = "Subject" };

        var trackingContext = InterceptorSubjectContext
            .Create()
            .WithContextInheritance();

        // Act
        ((IInterceptorSubject)subject).AttachToContext(trackingContext);

        // Assert
        Assert.True(((IInterceptorSubject)subject).IsAttached());
        Assert.Same(trackingContext, ((IInterceptorSubject)subject).TryGetAttachContext());
        Assert.NotNull(((IInterceptorSubject)subject).Context.TryGetLifecycleInterceptor());
    }

    [Fact]
    public void WhenAnInterceptorIsRegisteredAfterTheAttach_ThenItReceivesNoUnpairedDetach()
    {
        // Behaviour change 16: the detach notifies exactly the set the attach resolved.

        // Arrange
        var attachContext = InterceptorSubjectContext.Create();
        var lateDetaches = 0;
        var earlyDetaches = 0;

        attachContext.WithService(
            () => new CountingLifecycleInterceptor(() => Interlocked.Increment(ref earlyDetaches)),
            _ => false);

        var subject = new Person { FirstName = "Subject" };
        ((IInterceptorSubject)subject).AttachToContext(attachContext);

        attachContext.WithService(
            () => new CountingLifecycleInterceptor(() => Interlocked.Increment(ref lateDetaches)),
            _ => false);

        // Act
        ((IInterceptorSubject)subject).DetachFromContext(attachContext);

        // Assert
        Assert.Equal(1, earlyDetaches);
        Assert.Equal(0, lateDetaches);
    }

    [Fact]
    public void WhenRemoveFallbackContextTargetsTheAttachEdge_ThenItThrows()
    {
        // Behaviour change 2's second half. The guard hook has no other coverage: every migrated
        // test now goes through DetachFromContext and never exercises the rejection.

        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithContextInheritance();

        var subject = new Person { FirstName = "Subject" };
        ((IInterceptorSubject)subject).AttachToContext(context);

        // Act & Assert
        Assert.Throws<InvalidOperationException>(
            () => ((IInterceptorSubject)subject).Context.RemoveFallbackContext(context));

        Assert.True(((IInterceptorSubject)subject).IsAttached());
        Assert.Same(context, ((IInterceptorSubject)subject).TryGetAttachContext());
    }

    [Fact]
    public void WhenAnInterceptorsContextLeavesTheChain_ThenItStillReceivesTheDetachItWasOwed()
    {
        // Behaviour change 16's second half. Re-resolving at detach would give this interceptor
        // nothing, leaking whatever per-subject state it took at attach.

        // Arrange
        var detaches = 0;
        var departingContext = InterceptorSubjectContext
            .Create()
            .WithService(() => new CountingLifecycleInterceptor(() => Interlocked.Increment(ref detaches)), _ => false);

        var attachContext = InterceptorSubjectContext.Create();
        attachContext.AddFallbackContext(departingContext);

        var subject = new Person { FirstName = "Subject" };
        ((IInterceptorSubject)subject).AttachToContext(attachContext);

        // The interceptor's context leaves the chain after the attach recorded it.
        attachContext.RemoveFallbackContext(departingContext);
        Assert.Empty(attachContext.GetServices<ILifecycleInterceptor>());

        // Act
        ((IInterceptorSubject)subject).DetachFromContext(attachContext);

        // Assert
        Assert.Equal(1, detaches);
    }

    [Fact]
    public void WhenPropertyOwnedSubjectIsRootAttachedIntoAnotherGraph_ThenNoRecordAndNoEdgeArePublished()
    {
        // The directed test spec section 9 asks for. The ownership read in TryRecordAttachContext
        // is the only thing preventing the publish here, not the first of two defences: delete it
        // and nothing throws at all. Once the record and the edge into contextB are published, the
        // subject's own context resolves both graphs' interceptors, so ClaimOwnership's membership
        // predicate finds graph A's interceptor in that set and the claim succeeds. Reading
        // ownership before the edge exists is therefore load-bearing on its own.

        // Arrange
        var contextA = InterceptorSubjectContext
            .Create()
            .WithContextInheritance();

        var contextB = InterceptorSubjectContext
            .Create()
            .WithContextInheritance();

        var parentA = new Person(contextA) { FirstName = "ParentA" };
        var owned = new Person { FirstName = "Owned" };
        parentA.Mother = owned;

        var baseline = UsedByContextsProbe.Count(contextB);

        // Act & Assert
        Assert.Throws<InvalidOperationException>(
            () => ((IInterceptorSubject)owned).AttachToContext(contextB));

        Assert.Null(((IInterceptorSubject)owned).TryGetAttachContext());
        Assert.Equal(baseline, UsedByContextsProbe.Count(contextB));
    }

    [Fact]
    public void WhenConnectorItemIsAssignedUnderItsAttachParent_ThenItKeepsTheAttachEdgeAndGetsNoLink()
    {
        // Kills the mutant that sets the link and releases the attach edge instead of skipping the
        // link. Nothing else observes the record after a connector-shaped assignment.

        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithContextInheritance();

        var parent = new Person(context) { FirstName = "Parent" };
        var parentContext = ((IInterceptorSubject)parent).Context;

        var item = new Person { FirstName = "Item" };
        ((IInterceptorSubject)item).AttachToContext(parentContext);

        // Act
        parent.Mother = item;

        // Assert: the record still names the attach context, so the edge was not traded for a link.
        Assert.Same(parentContext, ((IInterceptorSubject)item).TryGetAttachContext());
        Assert.Equal(1, item.GetReferenceCount());
        Assert.NotNull(((IInterceptorSubject)item).TryGetRegisteredSubject());
    }

    [Fact]
    public void WhenRootAttachedSubjectGainsItsFirstParent_ThenTheSubtreeDescentDoesNotRunAgain()
    {
        // Kills the mutant that gates the descent on ReferenceCount == 1 instead of IsContextAttach.
        // Only this shape separates the two: the item is already in the ledger when it gains its
        // first parent, so IsContextAttach is false while the count becomes 1. Under the mutant the
        // descent re-runs AttachSubjectToContext over an already-attached subtree, re-seeding its
        // reconciliation baseline from the backing store. The spy counts that invocation directly,
        // because the re-seeding leaves nothing else observable behind.

        // Arrange
        var attachCount = 0;
        var context = InterceptorSubjectContext
            .Create()
            .WithContextInheritance()
            .WithService(() => new AttachCountingLifecycleInterceptor(() => attachCount++), _ => false);

        var parent = new Person(context) { FirstName = "Parent" };
        var parentContext = ((IInterceptorSubject)parent).Context;

        var item = new Person { FirstName = "Item", Mother = new Person { FirstName = "Child" } };
        ((IInterceptorSubject)item).AttachToContext(parentContext);

        // The parent's constructor attach, the item's root attach, and the descent onto the item's
        // own child, which is the pass the assignment below must not repeat.
        var attachesBeforeAssignment = attachCount;

        // Act
        parent.Father = item;

        // Assert
        Assert.Equal(3, attachesBeforeAssignment);
        Assert.Equal(attachesBeforeAssignment, attachCount);
    }

    [Fact]
    public void WhenHandlerRootAttachesTheSubjectDuringItsOwnDetach_ThenItThrows()
    {
        // Behaviour change 17 through the OTHER attach entry point. The property-path variant is
        // covered in commit 2; this one reaches ThrowIfDetachIsUnwinding via AttachRootSubject.

        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithContextInheritance();

        Exception? caught = null;
        var parent = new Person(context) { FirstName = "Parent" };
        context.WithService(() => new RootReAttachingHandler(exception => caught = exception));

        var child = new Person { FirstName = "Child" };
        parent.Mother = child;

        // Act
        parent.Mother = null;

        // Assert
        Assert.IsType<InvalidOperationException>(caught);
        Assert.Equal(0, child.GetReferenceCount());
    }

    [Fact]
    public void WhenSubjectReferencesItself_ThenNoSelfLinkIsPublished()
    {
        // Kills the mutant that deletes the self-context guard. The link itself has to be asserted:
        // with the guard gone the executor holds its attach edge and a link to itself, which is two
        // edges and therefore no delegation at all, and the service walk skips the self-parent
        // because the visited set already holds the executor. Read, write and reference count all
        // still succeed in that state, so only the absent link tells the two apart.

        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithContextInheritance();

        var subject = new Person(context) { FirstName = "Subject" };

        // Act
        subject.Mother = subject;

        // Assert
        Assert.False(((IInterceptorSubject)subject).GetExecutor().HasParentContext);

        // The consumer-visible half: still readable and writable, which a self-delegating context
        // would not be.
        subject.LastName = "written after the self reference";
        Assert.Equal("written after the self reference", subject.LastName);
        Assert.Equal(1, subject.GetReferenceCount());
    }

    [Fact]
    public void WhenASecondLifecycleBearingContextIsAdded_ThenItThrowsEvenThoughARecordExists()
    {
        // Kills the mutant that makes the AddFallbackContext guard test only for a non-null record
        // rather than for record identity. Without it the subject resolves graph B's interceptors
        // while being absent from B's ledger and registry.

        // Arrange
        var contextA = InterceptorSubjectContext
            .Create()
            .WithContextInheritance();

        var contextB = InterceptorSubjectContext
            .Create()
            .WithContextInheritance();

        var subject = new Person { FirstName = "Subject" };
        ((IInterceptorSubject)subject).AttachToContext(contextA);

        // Act & Assert
        Assert.Throws<InvalidOperationException>(
            () => ((IInterceptorSubject)subject).Context.AddFallbackContext(contextB));

        Assert.Same(contextA, ((IInterceptorSubject)subject).TryGetAttachContext());
    }

    private class ReAttachingHandler(Person target, Action<Exception> onThrow) : ILifecycleHandler
    {
        public void HandleLifecycleChange(SubjectLifecycleChange change)
        {
            if (!change.IsPropertyReferenceRemoved || change.ReferenceCount != 0)
            {
                return;
            }

            try
            {
                target.Father = (Person)change.Subject;
            }
            catch (Exception exception)
            {
                onThrow(exception);
            }
        }
    }

    private class RootReAttachingHandler(Action<Exception> onThrow) : ILifecycleHandler
    {
        public void HandleLifecycleChange(SubjectLifecycleChange change)
        {
            if (!change.IsPropertyReferenceRemoved || change.ReferenceCount != 0)
            {
                return;
            }

            try
            {
                change.Subject.AttachToContext(change.Subject.Context);
            }
            catch (Exception exception)
            {
                onThrow(exception);
            }
        }
    }

    private class ThrowingDetachHandler : ILifecycleHandler
    {
        public void HandleLifecycleChange(SubjectLifecycleChange change)
        {
            if (change.IsContextDetach)
            {
                throw new InvalidOperationException("detach handler failed");
            }
        }
    }

    private class MarkerService;

    private class OwnMarkerService;

    private class CountingLifecycleInterceptor(Action onDetach) : ILifecycleInterceptor
    {
        public void AttachSubjectToContext(IInterceptorSubject subject)
        {
        }

        public void DetachSubjectFromContext(IInterceptorSubject subject)
        {
            onDetach();
        }
    }

    private class AttachCountingLifecycleInterceptor(Action onAttach) : ILifecycleInterceptor
    {
        public void AttachSubjectToContext(IInterceptorSubject subject)
        {
            onAttach();
        }

        public void DetachSubjectFromContext(IInterceptorSubject subject)
        {
        }
    }
}
