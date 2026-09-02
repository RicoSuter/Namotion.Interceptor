using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Parent;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// Adversarial review probe for RollbackRejectedAttach: it removes the root's committed edges while
/// the root still carries its anchor, so a back edge that already attached the root reports the root
/// as "still held" and survives the drain. The anchor is only cleared afterwards, and the final
/// claim hand-back skips anything the graph still owns.
/// </summary>
public class AdversarialRollbackTests
{
    [Fact]
    public void WhenAnAttachIsRejectedAfterABackEdgeAttachedTheRoot_ThenTheRootIsFullyRolledBack()
    {
        // Arrange: root -> childA -> root is the everyday back reference, and childB is what the
        // attach callback refuses. The refusal happens after the back edge already published the
        // root into the graph.
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();

        var root = new Person { FirstName = "root" };
        var childA = new Person { FirstName = "A" };
        var childB = new Person { FirstName = "B" };

        root.Father = childA;
        childA.Father = root;
        root.Mother = childB;

        context.TryGetLifecycleInterceptor()!.SubjectAttached += change =>
        {
            if (ReferenceEquals(change.Subject, childB))
            {
                throw new InvalidOperationException("callback refuses childB");
            }
        };

        // Act
        var exception = Record.Exception(() => ((IInterceptorSubject)root).AttachToContext(context));

        // Assert: the attach was rejected...
        Assert.NotNull(exception);

        // ...so nothing it touched may stay behind. The root is the one the rollback exists for.
        var graph = ((LifecycleInterceptor)context.TryGetService<ILifecycleInterceptor>()!).Graph;
        var rootSubject = (IInterceptorSubject)root;

        Assert.False(graph.IsOwned(root),
            "the root is still owned by the graph after a rejected attach");
        Assert.Null(rootSubject.TryGetContext());
        Assert.Null(((IInterceptorSubject)childA).TryGetContext());
        Assert.Null(((IInterceptorSubject)childB).TryGetContext());
    }

    [Fact]
    public void WhenAnAttachIsRejectedAfterABackEdgeAttachedTheRoot_ThenTheRootCanStillBeDetached()
    {
        // Arrange: same shape, but this asserts the escape hatch the rollback documentation
        // promises ("the root is still attached and detachable rather than stripped").
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();

        var root = new Person { FirstName = "root" };
        var childA = new Person { FirstName = "A" };
        var childB = new Person { FirstName = "B" };

        root.Father = childA;
        childA.Father = root;
        root.Mother = childB;

        context.TryGetLifecycleInterceptor()!.SubjectAttached += change =>
        {
            if (ReferenceEquals(change.Subject, childB))
            {
                throw new InvalidOperationException("callback refuses childB");
            }
        };

        Record.Exception(() => ((IInterceptorSubject)root).AttachToContext(context));

        // Act & Assert: either the root is gone, or it must be detachable. Anything else is a
        // subject stuck in the context forever.
        var rootSubject = (IInterceptorSubject)root;
        if (rootSubject.TryGetContext() is null)
        {
            return;
        }

        var detachException = Record.Exception(() => rootSubject.DetachFromContext(context));
        Assert.Null(detachException);
        Assert.Null(rootSubject.TryGetContext());
    }
}
