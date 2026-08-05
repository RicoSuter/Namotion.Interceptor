using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// Issues #402 defects 3, 4 and 5, the two-graph half-attach from spec section 2, and re-attach
/// during detach. Each must fail against unmodified master for the reason named in its comment.
/// Defect 2 (concurrent detach running the interceptors twice) needs the new API and lives in
/// Task 4.
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
            ((IInterceptorSubject)subject).Context.AddFallbackContext(loopA);
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
        ((IInterceptorSubject)subject).Context.AddFallbackContext(attachContext);

        var attachedCount = UsedByContextsProbe.Count(attachContext);

        // attachContext -> midContext -> attachContext, both pure delegators, so resolving raises.
        midContext.RemoveFallbackContext(graphContext);
        midContext.AddFallbackContext(attachContext);

        // Act: the detach raises either way, because the descent resolves handlers through the
        // subject's own now-cyclic chain. What this pins is that the edge comes out regardless.
        Assert.ThrowsAny<InvalidOperationException>(
            () => ((IInterceptorSubject)subject).Context.RemoveFallbackContext(attachContext));

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
        ((IInterceptorSubject)subject).Context.AddFallbackContext(contextA);

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
}
