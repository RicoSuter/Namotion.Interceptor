using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

public class NestedSeedingGetterFrameTests
{
    [Fact]
    public void WhenNestedSeedWritesAnOuterActiveGetterProperty_ThenOuterGetterIsNotReadRecursively()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithLifecycle();
        var root = new PayloadNode(context);
        var otherRoot = new PayloadNode(context);
        var replacement = new PayloadNode();
        var outer = new ReadHookSegmentHolder();
        var inner = new ReadHookSegmentHolder();
        var outerReads = 0;
        outer.OnRead = () =>
        {
            if (outer.GetReferenceCount() == 0) return;
            Assert.Equal(1, ++outerReads);
            otherRoot.Payload = inner;
        };
        inner.OnRead = () =>
        {
            if (inner.GetReferenceCount() == 0) return;
            inner.OnRead = null;
            outer.Children = new([replacement]);
        };

        // Act
        root.Payload = outer;
        outer.OnRead = null;

        // Assert
        Assert.Equal(1, outerReads);
        Assert.Same(replacement, Assert.Single(outer.Children));
        Assert.Same(context, replacement.TryGetContext());
        Assert.Equal(1, replacement.GetReferenceCount());
        root.Payload = null;
        otherRoot.Payload = null;
        Assert.Null(outer.TryGetContext());
        Assert.Null(inner.TryGetContext());
        Assert.Null(replacement.TryGetContext());
    }
}
