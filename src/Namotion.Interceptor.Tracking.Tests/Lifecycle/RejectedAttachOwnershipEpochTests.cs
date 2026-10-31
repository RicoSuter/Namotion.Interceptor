using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

public class RejectedAttachOwnershipEpochTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhenASeedingGetterReattachesItsRootBeforeThrowing_ThenRollbackPreservesTheNewAnchor(bool retainOwnership)
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var outside = retainOwnership ? new PayloadNode(context) : new PayloadNode();
        var discarded = new PayloadNode();
        var completed = new PayloadNode();
        var replacement = new PayloadNode();
        var trigger = new ReadHookSegmentHolder { Children = new([discarded]) };
        var root = new PayloadNode { Payload = new object[] { completed, trigger } };
        var failure = new InvalidOperationException("older seed failed after root reattachment");
        var reattached = false;
        trigger.OnRead = () =>
        {
            if (trigger.GetReferenceCount() == 0) return;
            trigger.OnRead = null;
            if (retainOwnership) outside.Payload = root;
            root.DetachFromContext(context);
            root.Payload = replacement;
            root.AttachToContext(context);
            reattached = true;
            throw failure;
        };

        // Act
        var exception = Record.Exception(() => root.AttachToContext(context));

        // Assert
        Assert.True(reattached);
        Assert.Same(failure, exception);
        Assert.Equal(SubjectAttachmentAnchorKind.Explicit, ((IInterceptorSubject)root).Executor.AttachmentAnchor);
        IInterceptorSubject[] roots = retainOwnership ? [outside, root] : [root];
        SupportContractAssertions.Settled(context, roots, outside, root, discarded, completed, replacement, trigger);
        root.DetachFromContext(context);
        if (retainOwnership)
        {
            outside.Payload = null;
            outside.DetachFromContext(context);
        }
        SupportContractAssertions.Settled(context, [], outside, root, discarded, completed, replacement, trigger);
    }

    [Fact]
    public void WhenARejectedProvisionalRootWasAdoptedDuringSeeding_ThenOutsideSupportAndUnusedClaimCleanupSurvive()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var outside = new PayloadNode(context);
        var completed = new PayloadNode();
        var pending = new PayloadNode();
        var trigger = new ReadHookSegmentHolder { Children = new([pending]) };
        var root = new PayloadNode { Payload = new object[] { completed, trigger } };
        var failure = new InvalidOperationException("seed failed after provisional adoption");
        trigger.OnRead = () =>
        {
            if (trigger.GetReferenceCount() == 0) return;
            trigger.OnRead = null;
            outside.Payload = root;
            throw failure;
        };

        // Act
        var exception = Record.Exception(() => context.GetService<ILifecycleInterceptor>()
            .AttachSubjectToContext(root, context, SubjectAttachmentAnchorKind.Provisional));

        // Assert
        Assert.Same(failure, exception);
        Assert.Equal(SubjectAttachmentAnchorKind.None, ((IInterceptorSubject)root).Executor.AttachmentAnchor);
        Assert.Same(context, completed.TryGetContext());
        Assert.Same(context, trigger.TryGetContext());
        Assert.NotNull(completed.TryGetRegisteredSubject());
        Assert.NotNull(trigger.TryGetRegisteredSubject());
        Assert.Null(pending.TryGetContext());
        outside.Payload = null;
        SupportContractAssertions.Settled(context, [outside], outside, root, completed, trigger, pending);
        outside.DetachFromContext(context);
        SupportContractAssertions.Settled(context, [], outside, root, completed, trigger, pending);
    }
}
