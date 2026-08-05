using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// The known limits of the context inheritance redesign, each pinned at its current documented
/// outcome so that a later change which worsens one fails visibly instead of silently.
///
/// A failure here is not automatically a bug. It means either someone improved the behaviour, in
/// which case update the test and the design document deliberately, or someone regressed it. Every
/// case below is recorded in docs/design/tracking-lifecycle.md, section "Known Gaps", one entry per
/// test.
/// </summary>
public class KnownGapTests
{
    [Fact]
    public async Task WhenAttachRacesDetachOnTheSameRoot_ThenNoInvariantOtherThanEdgeAgreementHolds()
    {
        // #402 defect 1, explicitly NOT closed. Both operations are two steps and are not atomic
        // against each other: a detach can clear the record, an attach can then record and publish,
        // and the detach's second step then removes the edge, leaving a record with no edge.
        //
        // So this test deliberately does NOT assert that the record and the edge agree. That
        // agreement is exactly what defect 1 breaks, and asserting it would be asserting a fix we
        // did not make. What it pins is the weaker property that must survive: whatever the race
        // leaves behind, the subject is still cleanly re-attachable, so no round can wedge it in a
        // state no consumer call can leave.

        for (var round = 0; round < 50; round++)
        {
            // Arrange
            var context = InterceptorSubjectContext
                .Create()
                .WithContextInheritance();

            var subject = new Person { FirstName = "Subject" };
            ((IInterceptorSubject)subject).AttachToContext(context);

            using var start = new ManualResetEventSlim(false);

            // Act
            var racers = new[]
            {
                Task.Factory.StartNew(() =>
                {
                    start.Wait();
                    try { ((IInterceptorSubject)subject).AttachToContext(context); }
                    catch (InvalidOperationException) { }
                }, TaskCreationOptions.LongRunning),
                Task.Factory.StartNew(() =>
                {
                    start.Wait();
                    try { ((IInterceptorSubject)subject).DetachFromContext(context); }
                    catch (InvalidOperationException) { }
                }, TaskCreationOptions.LongRunning)
            };

            start.Set();
            await Task.WhenAll(racers);

            // Assert: whichever of the four record/edge combinations the round produced, a full
            // attach and detach round still completes and leaves the subject in a graph-free state.
            // A wedged residue throws out of one of these two calls.
            ((IInterceptorSubject)subject).AttachToContext(context);
            ((IInterceptorSubject)subject).DetachFromContext(context);

            Assert.Null(((IInterceptorSubject)subject).TryGetAttachContext());
            Assert.False(((IInterceptorSubject)subject).IsAttached());
        }
    }

    [Fact]
    public async Task WhenAddingTheAttachContextDuringTheDetachWindow_ThenItThrows()
    {
        // #411, whose silent form is gone. DetachFromContext clears the record before the
        // interceptor loop, so the re-add is no longer naming the recorded attach context and the
        // guard rejects it. The issue stays open because the caller still cannot complete the add.

        // Arrange
        using var insideDetach = new ManualResetEventSlim(false);
        using var addAttempted = new ManualResetEventSlim(false);
        Exception? caught = null;
        var addObserved = false;

        var subject = new Person { FirstName = "Subject" };
        var context = InterceptorSubjectContext.Create();
        context.WithService(
            () => new RendezvousLifecycleInterceptor(() =>
            {
                insideDetach.Set();
                addObserved = addAttempted.Wait(TimeSpan.FromSeconds(5));
            }),
            _ => false);

        ((IInterceptorSubject)subject).AttachToContext(context);

        var detach = Task.Factory.StartNew(
            () => ((IInterceptorSubject)subject).DetachFromContext(context),
            TaskCreationOptions.LongRunning);

        // Act. Both halves of the rendezvous are asserted, because a timed-out wait would let the
        // add run outside the detach window and the throw below would then have another cause.
        Assert.True(insideDetach.Wait(TimeSpan.FromSeconds(5)), "The detach never reached the interceptor.");
        try
        {
            ((IInterceptorSubject)subject).Context.AddFallbackContext(context);
        }
        catch (Exception exception)
        {
            caught = exception;
        }

        addAttempted.Set();

        // Awaited rather than Task.Wait: xUnit1031 rejects a blocking join in a test body.
        await detach.WaitAsync(TimeSpan.FromSeconds(5));

        // Assert
        Assert.True(addObserved, "The detach left the interceptor before the add was attempted.");
        Assert.IsType<InvalidOperationException>(caught);
    }

    [Fact]
    public void WhenLinkedParentLeavesWhileAnotherHoldsTheSubject_ThenTheSubjectGoesDark()
    {
        // #410 symptom 2, first shape, not closed. The subject stays attached and referenced while
        // resolving nothing at all, which is more severe than the issue predicts.

        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        var parent1 = new Person(context) { FirstName = "P1" };
        var parent2 = new Person(context) { FirstName = "P2" };
        var shared = new Person { FirstName = "Shared" };

        parent1.Mother = shared;
        parent2.Mother = shared;

        // Act: the parent the link points at leaves the graph while parent2 still holds the subject.
        ((IInterceptorSubject)parent1).DetachFromContext(context);

        // Assert: the gap. Change this only deliberately.
        Assert.Equal(1, shared.GetReferenceCount());
        Assert.Empty(((IInterceptorSubject)shared).Context.GetServices<IWriteInterceptor>());
    }

    [Fact]
    public void WhenConnectorItemsAttachParentLeaves_ThenTheItemGoesDark()
    {
        // #410 symptom 2, second shape. The item's only edge is its attach edge, because the link
        // gate deliberately skips a context the attach edge already names.

        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        var parent = new Person(context) { FirstName = "Parent" };
        var holder = new Person(context) { FirstName = "Holder" };

        var item = new Person { FirstName = "Item" };
        ((IInterceptorSubject)item).AttachToContext(((IInterceptorSubject)parent).Context);

        // The FIRST reference must be the attach parent, so the link gate skips: it fires only at
        // reference count 1, and there it sees a context the attach edge already names. Referencing
        // the item from the holder first would set a link to the holder instead and the item would
        // survive the parent leaving, which is not the shape #410 describes.
        parent.Mother = item;
        holder.Mother = item;

        // Act
        ((IInterceptorSubject)parent).DetachFromContext(context);

        // Assert: the gap. The item is still referenced and still attached, and its only edge is an
        // attach edge into a context that has itself left the graph.
        Assert.Equal(1, item.GetReferenceCount());
        Assert.Empty(((IInterceptorSubject)item).Context.GetServices<IWriteInterceptor>());
    }

    [Fact]
    public void WhenCrossGraphRejectionHappensMidBatch_ThenEarlierItemsStayAttached()
    {
        // #384's shape: WriteProperty commits through next() before taking the lock, so the backing
        // store keeps the value and earlier items of the batch are already attached.

        // Arrange
        var contextA = InterceptorSubjectContext.Create().WithContextInheritance();
        var contextB = InterceptorSubjectContext.Create().WithContextInheritance();

        var ownerA = new Person(contextA) { FirstName = "OwnerA" };
        var owned = new Person { FirstName = "Owned" };
        ownerA.Mother = owned;

        var parentB = new Person(contextB) { FirstName = "ParentB" };
        var free = new Person { FirstName = "Free" };

        var ownedReferencesInGraphA = owned.GetReferenceCount();

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => parentB.Children = [free, owned]);

        // The gap: the write is committed and the earlier item is attached.
        Assert.Equal(2, parentB.Children.Length);
        Assert.Equal(1, free.GetReferenceCount());

        // The rejected item keeps exactly the references its own graph gave it, because
        // ClaimOwnership runs ahead of every mutation in AttachToProperty. So the batch is half
        // applied rather than leaving the item counted in two graphs.
        Assert.Equal(1, ownedReferencesInGraphA);
        Assert.Equal(ownedReferencesInGraphA, owned.GetReferenceCount());
    }

    [Fact]
    public void WhenAttachHandlerThrowsPartWay_ThenTheLifecycleResidueRemains()
    {
        // The rollback in AttachToContext's catch clears this context's own state only. Anything the
        // lifecycle system already did stays, which is #384's rollback problem.

        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithContextInheritance();

        context.WithService(() => new ThrowingAttachHandler());

        var child = new Person { FirstName = "Child" };
        var root = new Person { FirstName = "Root", Mother = child };

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => ((IInterceptorSubject)root).AttachToContext(context));

        // The gap: the root's own record and edge are rolled back, the child's attach is not.
        Assert.Null(((IInterceptorSubject)root).TryGetAttachContext());
        Assert.Equal(1, child.GetReferenceCount());
    }

    [Fact]
    public void WhenFallbackCycleExists_ThenASubjectInheritsItsOwnDescendantsSubtreeService()
    {
        // Predates this design and is not fixed by it. Contradicts what ContextSubtreeServiceTests
        // documents about subtree scoping.

        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithContextInheritance();

        var a = new Person(context) { FirstName = "A" };
        var b = new Person { FirstName = "B" };

        a.Mother = b;
        b.Father = a;

        // Act
        ((IInterceptorSubject)b).Context.AddService(new SubtreeMarker());

        // Assert: the gap. A resolves a service registered on its own descendant.
        Assert.Single(((IInterceptorSubject)a).Context.GetServices<SubtreeMarker>());
    }

    [Fact]
    public void WhenTwoRootContextsShareOneTrackingContext_ThenTheCrossGraphRejectionDoesNotApply()
    {
        // The owner is an ILifecycleInterceptor reference, so two root contexts sharing one tracking
        // context count as one graph while having two registries. The two-graph finding from spec
        // section 2 is not closed in that configuration.

        // Arrange. The fallback must be wired BEFORE WithRegistry: WithService skips only when the
        // service type already RESOLVES through the chain, so registering first would give each
        // root its own LifecycleInterceptor, two genuinely separate graphs, and the cross-graph
        // rejection would fire correctly rather than demonstrating the gap.
        var trackingContext = InterceptorSubjectContext
            .Create()
            .WithContextInheritance();

        var rootA = InterceptorSubjectContext.Create();
        rootA.AddFallbackContext(trackingContext);
        rootA.WithRegistry();

        var rootB = InterceptorSubjectContext.Create();
        rootB.AddFallbackContext(trackingContext);
        rootB.WithRegistry();

        // One lifecycle interceptor, two registries.
        Assert.Same(rootA.TryGetLifecycleInterceptor(), rootB.TryGetLifecycleInterceptor());
        Assert.NotSame(rootA.TryGetService<ISubjectRegistry>(), rootB.TryGetService<ISubjectRegistry>());

        var parentA = new Person(rootA) { FirstName = "ParentA" };
        var parentB = new Person(rootB) { FirstName = "ParentB" };
        var shared = new Person { FirstName = "Shared" };

        // Act: no throw, because both graphs resolve the same lifecycle interceptor.
        parentA.Mother = shared;
        parentB.Mother = shared;

        // Assert: the gap. Both registries index it, one resolution wins.
        Assert.Equal(2, shared.GetReferenceCount());
        Assert.Contains(shared, rootA.TryGetService<ISubjectRegistry>()!.KnownSubjects.Keys);
        Assert.Contains(shared, rootB.TryGetService<ISubjectRegistry>()!.KnownSubjects.Keys);
    }

    [Fact]
    public void WhenConstructorAttachedSubtreeIsRemovedFromItsParent_ThenTheCascadeIsBottomUp()
    {
        // A constructor-attached subject that owns a child and is then placed under a parent is the
        // only shape whose detach cascade order moved with this design, and exactly one snapshot
        // covers it incidentally. Pinned here deliberately.
        //
        // The order below is the bottom-up cascade the rest of the system already produces: the
        // descent runs from inside the handler chain, so the deeper subject's handlers complete
        // before the handler that triggered the descent sees the subject it was called for. Its
        // twin for a subject that was never constructor-attached is asserted in
        // AttachOrderCharacterizationTests.

        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithContextInheritance();

        var middle = new Person(context) { FirstName = "Middle" };
        var leaf = new Person { FirstName = "Leaf" };
        middle.Mother = leaf;

        var parent = new Person(context) { FirstName = "Parent" };
        parent.Mother = middle;

        var handlerLog = new List<string>();
        context.WithService(() => new RecordingDetachHandler(handlerLog));

        // Act
        parent.Mother = null;

        // Assert
        Assert.Equal(["Leaf", "Middle"], handlerLog);
    }

    private class SubtreeMarker;

    private class RecordingDetachHandler(List<string> log) : ILifecycleHandler
    {
        public void HandleLifecycleChange(SubjectLifecycleChange change)
        {
            if (change is { IsPropertyReferenceRemoved: true, ReferenceCount: 0 })
            {
                log.Add(((Person)change.Subject).FirstName ?? "?");
            }
        }
    }

    private class ThrowingAttachHandler : ILifecycleHandler
    {
        public void HandleLifecycleChange(SubjectLifecycleChange change)
        {
            if (change is { IsContextAttach: true, Property: null })
            {
                throw new InvalidOperationException("attach handler failed");
            }
        }
    }

    private class RendezvousLifecycleInterceptor(Action onDetach) : ILifecycleInterceptor
    {
        public void AttachSubjectToContext(IInterceptorSubject subject)
        {
        }

        public void DetachSubjectFromContext(IInterceptorSubject subject)
        {
            onDetach();
        }
    }
}
