using System.Threading;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.WebSocket.Internal;
using Namotion.Interceptor.WebSocket.Tests.Integration;
using Xunit;

namespace Namotion.Interceptor.WebSocket.Tests.Internal;

public class StateDigestTests
{
    private static IInterceptorSubjectContext CreateContext()
    {
        return InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();
    }

    [Fact]
    public void WhenTwoGraphsHaveSameState_ThenDigestsAreEqual()
    {
        // Arrange
        var first = BuildGraph("RootName", number: 42m, childLabel: "child", childValue: 7);
        var second = BuildGraph("RootName", number: 42m, childLabel: "child", childValue: 7);

        // Act
        var firstDigest = StateDigest.Compute(first);
        var secondDigest = StateDigest.Compute(second);

        // Assert
        Assert.NotEqual(string.Empty, firstDigest);
        Assert.Equal(firstDigest, secondDigest);
    }

    [Fact]
    public void WhenAValuePropertyDiffers_ThenDigestsDiffer()
    {
        // Arrange
        var first = BuildGraph("RootName", number: 42m, childLabel: "child", childValue: 7);
        var second = BuildGraph("RootName", number: 42m, childLabel: "child", childValue: 8);

        // Act
        var firstDigest = StateDigest.Compute(first);
        var secondDigest = StateDigest.Compute(second);

        // Assert
        Assert.NotEqual(firstDigest, secondDigest);
    }

    [Fact]
    public void WhenARootValueDiffers_ThenDigestsDiffer()
    {
        // Arrange
        var first = BuildGraph("RootName", number: 42m, childLabel: "child", childValue: 7);
        var second = BuildGraph("DifferentName", number: 42m, childLabel: "child", childValue: 7);

        // Act
        var firstDigest = StateDigest.Compute(first);
        var secondDigest = StateDigest.Compute(second);

        // Assert
        Assert.NotEqual(firstDigest, secondDigest);
    }

    [Fact]
    public void WhenOnlyWriteTimestampsDiffer_ThenDigestsAreEqual()
    {
        // Arrange
        var first = BuildGraph("RootName", number: 42m, childLabel: "child", childValue: 7);

        // Write the identical values into a second graph at a later wall-clock time so that
        // WithFullPropertyTracking stamps different write timestamps. The digest must ignore them.
        Thread.Sleep(5);
        var second = BuildGraph("RootName", number: 42m, childLabel: "child", childValue: 7);

        // Sanity check: the write timestamps actually differ between the two graphs.
        var firstTimestamp = first.GetPropertyReference("Name").TryGetWriteTimestamp();
        var secondTimestamp = second.GetPropertyReference("Name").TryGetWriteTimestamp();
        Assert.NotNull(firstTimestamp);
        Assert.NotNull(secondTimestamp);
        Assert.NotEqual(firstTimestamp, secondTimestamp);

        // Act
        var firstDigest = StateDigest.Compute(first);
        var secondDigest = StateDigest.Compute(second);

        // Assert
        Assert.Equal(firstDigest, secondDigest);
    }

    [Fact]
    public void WhenASubjectIsMissing_ThenDigestsDiffer()
    {
        // Arrange
        var withChild = BuildGraph("RootName", number: 42m, childLabel: "child", childValue: 7);

        var withoutChild = CreateContext();
        var root = new TestRoot(withoutChild) { Name = "RootName", Number = 42m };
        root.GetPropertyReference("Name"); // ensure registration side effects are realized
        AssignStableIds(root);

        // Act
        var withChildDigest = StateDigest.Compute(withChild);
        var withoutChildDigest = StateDigest.Compute(root);

        // Assert
        Assert.NotEqual(withChildDigest, withoutChildDigest);
    }

    [Fact]
    public void WhenCollectionMembershipDiffers_ThenDigestsDiffer()
    {
        // Arrange
        var oneItemContext = CreateContext();
        var oneItemRoot = new TestRoot(oneItemContext)
        {
            Name = "Root",
            Items = [new TestItem(oneItemContext) { Label = "a", Value = 1 }]
        };
        AssignStableIds(oneItemRoot);

        var twoItemContext = CreateContext();
        var twoItemRoot = new TestRoot(twoItemContext)
        {
            Name = "Root",
            Items =
            [
                new TestItem(twoItemContext) { Label = "a", Value = 1 },
                new TestItem(twoItemContext) { Label = "b", Value = 2 }
            ]
        };
        AssignStableIds(twoItemRoot);

        // Act
        var oneItemDigest = StateDigest.Compute(oneItemRoot);
        var twoItemDigest = StateDigest.Compute(twoItemRoot);

        // Assert
        Assert.NotEqual(oneItemDigest, twoItemDigest);
    }

    [Fact]
    public void WhenNoRegistryConfigured_ThenDigestIsEmpty()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        var root = new TestRoot(context) { Name = "Root" };

        // Act
        var digest = StateDigest.Compute(root);

        // Assert
        Assert.Equal(string.Empty, digest);
    }

    // Builds a registry-backed graph with a root + one child reference, then assigns deterministic,
    // identical subject ids across graphs so two graphs in the same converged state hash identically.
    // This mirrors the protocol, where the client adopts the server's ids via Welcome/Update.
    private static TestRoot BuildGraph(string name, decimal number, string childLabel, int childValue)
    {
        var context = CreateContext();
        var root = new TestRoot(context)
        {
            Name = name,
            Number = number,
            Child = new TestItem(context) { Label = childLabel, Value = childValue }
        };

        AssignStableIds(root);
        return root;
    }

    // Assigns stable, position-based ids so equivalent graphs share ids (as they would after the
    // client adopts the server's ids during the protocol handshake).
    private static void AssignStableIds(TestRoot root)
    {
        root.SetSubjectId("root");

        if (root.Child is not null)
        {
            root.Child.SetSubjectId("child");
        }

        if (root.Items is not null)
        {
            for (var i = 0; i < root.Items.Length; i++)
            {
                root.Items[i].SetSubjectId($"item-{i}");
            }
        }
    }
}
