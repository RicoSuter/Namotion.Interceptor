using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

public class CallbackSupportGetterContinuationTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void WhenGetterReleasesAncestorDuringAttach_ThenNoDescendantRemainsOwned(int depth)
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithLifecycle();
        var root = new PayloadNode(context);
        var target = new PayloadNode();
        var wrapper = new ReadHookSegmentHolder { Children = new([target]) };
        var nodes = new List<PayloadNode>();
        object component = wrapper;
        for (var index = 0; index < depth; index++)
        {
            var node = new PayloadNode { Payload = component };
            nodes.Add(node);
            component = node;
        }
        wrapper.OnRead = () =>
        {
            if (root.Payload is not null)
            {
                wrapper.OnRead = null;
                root.Payload = null;
            }
        };

        // Act
        root.Payload = component;

        // Assert
        Assert.Null(root.Payload);
        Assert.Null(wrapper.TryGetContext());
        Assert.Null(target.TryGetContext());
        Assert.All(nodes, node => Assert.Null(node.TryGetContext()));
    }

    [Fact]
    public void WhenDescendantGetterRewritesTheSeedingProperty_ThenOnlyTheLatestChildrenAreOwned()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithLifecycle();
        var root = new PayloadNode(context);
        var stale = new PayloadNode();
        var replacement = new PayloadNode();
        var trigger = new ReadHookSegmentHolder();
        var branch = new PayloadNode { Payload = trigger };
        var wrapper = new ReadHookSegmentHolder { Children = new([branch, stale]) };
        trigger.OnRead = () =>
        {
            if (trigger.GetReferenceCount() > 0)
            {
                trigger.OnRead = null;
                wrapper.Children = new([replacement]);
            }
        };

        // Act
        root.Payload = wrapper;

        // Assert
        Assert.Same(replacement, Assert.Single(wrapper.Children));
        Assert.Same(context, replacement.TryGetContext());
        Assert.Equal(1, replacement.GetReferenceCount());
        Assert.Null(branch.TryGetContext());
        Assert.Null(trigger.TryGetContext());
        Assert.Null(stale.TryGetContext());
    }

    [Fact]
    public void WhenGetterReleasesAndReattachesTheSameOwner_ThenItsChildrenAttachOnce()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithLifecycle();
        var root = new PayloadNode(context);
        var target = new PayloadNode();
        var wrapper = new ReadHookSegmentHolder { Children = new([target]) };
        wrapper.OnRead = () =>
        {
            if (ReferenceEquals(root.Payload, wrapper))
            {
                wrapper.OnRead = null;
                root.Payload = null;
                root.Payload = wrapper;
            }
        };

        // Act
        root.Payload = wrapper;

        // Assert
        Assert.Same(wrapper, root.Payload);
        Assert.Same(context, wrapper.TryGetContext());
        Assert.Same(context, target.TryGetContext());
        Assert.Equal(1, wrapper.GetReferenceCount());
        Assert.Equal(1, target.GetReferenceCount());
    }

    [Fact]
    public void WhenUnpublishedRootGetterWritesTheStableBoxedSegment_ThenSeedingTerminates()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithLifecycle();
        var target = new PayloadNode();
        var stable = new ArraySegment<PayloadNode>([target]);
        var root = new ReadHookSegmentHolder { Children = stable };
        var reads = 0;
        root.OnRead = () =>
        {
            if (root.TryGetContext() is not null && root.GetReferenceCount() == 0)
            {
                if (++reads > 16) throw new InvalidOperationException("unbounded getter retry");
                root.Children = stable;
            }
        };

        // Act
        var exception = Record.Exception(() => root.AttachToContext(context));
        root.OnRead = null;

        // Assert
        Assert.Null(exception);
        Assert.True(reads > 0);
        Assert.Same(target, Assert.Single(root.Children));
        Assert.Same(context, target.TryGetContext());
        Assert.Equal(1, target.GetReferenceCount());
    }

    [Fact]
    public void WhenClaimedRootGetterAssignsForeignChild_ThenTheWriteIsRejectedBeforeStorage()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithLifecycle();
        var foreignContext = InterceptorSubjectContext.Create().WithLifecycle();
        var foreign = new PayloadNode(foreignContext);
        var target = new PayloadNode();
        var root = new ReadHookSegmentHolder { Children = new([target]) };
        Exception? writeException = null;
        root.OnRead = () =>
        {
            if (root.TryGetContext() is not null)
            {
                root.OnRead = null;
                writeException = Record.Exception(() => root.Children = new([foreign]));
            }
        };

        // Act
        var attachException = Record.Exception(() => root.AttachToContext(context));

        // Assert
        Assert.Null(attachException);
        Assert.IsType<InvalidOperationException>(writeException);
        Assert.Same(target, Assert.Single(root.Children));
        Assert.Same(context, target.TryGetContext());
        Assert.Equal(1, target.GetReferenceCount());
        Assert.Same(foreignContext, foreign.TryGetContext());
    }

    [Fact]
    public void WhenAChildGetterWritesTheStableBoxedSegment_ThenAttachTerminatesAndEdgesAgree()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithLifecycle();
        var root = new PayloadNode(context);
        var target = new PayloadNode();
        root.Payload = new PayloadNode { Child = target };
        var stable = new ArraySegment<PayloadNode>([target]);
        var wrapper = new ReadHookSegmentHolder { Children = stable };
        var reads = 0;
        wrapper.OnRead = () =>
        {
            if (ReferenceEquals(root.Payload, wrapper) && wrapper.GetReferenceCount() == 1)
            {
                if (++reads > 16) throw new InvalidOperationException("unbounded getter retry");
                wrapper.Children = stable;
            }
        };

        // Act
        var exception = Record.Exception(() => root.Payload = wrapper);
        wrapper.OnRead = null;

        // Assert
        Assert.Null(exception);
        Assert.True(reads > 0);
        Assert.Same(wrapper, root.Payload);
        Assert.Same(context, wrapper.TryGetContext());
        Assert.Same(context, target.TryGetContext());
        Assert.Equal(1, target.GetReferenceCount());

        // Act
        root.Payload = null;

        // Assert
        Assert.Null(wrapper.TryGetContext());
        Assert.Null(target.TryGetContext());
        Assert.Equal(0, wrapper.GetReferenceCount());
        Assert.Equal(0, target.GetReferenceCount());
    }

    [Fact]
    public void WhenGetterReplacesOwnerPropertyDuringAttach_ThenFinalGraphMatchesTheLatestWrite()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithLifecycle();
        var root = new PayloadNode(context);
        var target = new PayloadNode();
        var wrapper = new ReadHookSegmentHolder { Children = new([target]) };
        var replacement = new PayloadNode();
        wrapper.OnRead = () =>
        {
            if (ReferenceEquals(root.Payload, wrapper))
            {
                wrapper.OnRead = null;
                root.Payload = replacement;
            }
        };

        // Act
        root.Payload = wrapper;

        // Assert
        Assert.Same(replacement, root.Payload);
        Assert.Same(context, replacement.TryGetContext());
        Assert.Equal(1, replacement.GetReferenceCount());
        Assert.Null(wrapper.TryGetContext());
        Assert.Null(target.TryGetContext());
    }

    [Fact]
    public void WhenGetterReattachesItsOwnerAndThenWrites_ThenTheNewOwnershipReconcilesTheWrite()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithLifecycle();
        var root = new PayloadNode(context);
        var stale = new PayloadNode();
        var replacement = new PayloadNode();
        var wrapper = new ReadHookSegmentHolder { Children = new([stale]) };
        wrapper.OnRead = () =>
        {
            if (wrapper.GetReferenceCount() > 0)
            {
                wrapper.OnRead = null;
                root.Payload = null;
                root.Payload = wrapper;
                wrapper.Children = new([replacement]);
            }
        };

        // Act
        root.Payload = wrapper;

        // Assert
        Assert.Same(wrapper, root.Payload);
        Assert.Same(replacement, Assert.Single(wrapper.Children));
        Assert.Same(context, replacement.TryGetContext());
        Assert.Equal(1, replacement.GetReferenceCount());
        Assert.Null(stale.TryGetContext());
    }

    [Fact]
    public void WhenAChildGetterReplacesTheWritingProperty_ThenNoRemainingOldOccurrenceIsAttached()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithLifecycle();
        var root = new PayloadNode(context);
        var stale = new PayloadNode();
        var replacement = new PayloadNode();
        var trigger = new ReadHookSegmentHolder();
        var branch = new PayloadNode { Payload = trigger };
        trigger.OnRead = () =>
        {
            if (trigger.GetReferenceCount() > 0)
            {
                trigger.OnRead = null;
                root.Payload = replacement;
            }
        };

        // Act
        root.Payload = new[] { branch, stale };

        // Assert
        Assert.Same(replacement, root.Payload);
        Assert.Same(context, replacement.TryGetContext());
        Assert.Equal(1, replacement.GetReferenceCount());
        Assert.Null(branch.TryGetContext());
        Assert.Null(trigger.TryGetContext());
        Assert.Null(stale.TryGetContext());
    }

    [Fact]
    public void WhenAChildGetterKeepsAnOccurrenceTheWriteHadNotAttachedYet_ThenThatOccurrenceStillAttaches()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithLifecycle();
        var root = new PayloadNode(context);
        var retained = new PayloadNode();
        var trigger = new ReadHookSegmentHolder();
        var branch = new PayloadNode { Payload = trigger };
        trigger.OnRead = () =>
        {
            if (trigger.GetReferenceCount() > 0)
            {
                trigger.OnRead = null;
                root.Payload = new[] { retained };
            }
        };

        // Act
        root.Payload = new[] { branch, retained };

        // Assert
        Assert.Same(retained, Assert.Single((PayloadNode[])root.Payload));
        Assert.Same(context, retained.TryGetContext());
        Assert.Equal(1, retained.GetReferenceCount());
        Assert.Null(branch.TryGetContext());
        Assert.Null(trigger.TryGetContext());
    }
}
