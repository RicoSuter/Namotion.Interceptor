using Xunit;
using Namotion.Interceptor.ConnectorTester.Engine;
using Namotion.Interceptor.ConnectorTester.Model;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.ConnectorTester.Tests.Engine;

public class SnapshotComparerTests
{
    private static IInterceptorSubjectContext CreateContext()
        => InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithParents()
            .WithLifecycle();

    private static TestNode CreateLeaf(IInterceptorSubjectContext context, string stringValue, int intValue)
    {
        return new TestNode(context)
        {
            StringValue = stringValue,
            IntValue = intValue,
            DecimalValue = intValue,
            LongValue = intValue
        };
    }

    [Fact]
    public void WhenSnapshotsAreIdentical_ThenCapturesMatch()
    {
        // Arrange
        var contextA = CreateContext();
        var rootA = CreateLeaf(contextA, "x", 1);

        var contextB = CreateContext();
        var rootB = CreateLeaf(contextB, "x", 1);

        // Act
        var snapshotA = SnapshotComparer.Capture(rootA);
        var snapshotB = SnapshotComparer.Capture(rootB);

        // Assert
        Assert.Equal(snapshotA, snapshotB);
    }

    [Fact]
    public void WhenRootIdDiffersAcrossParticipants_ThenCapturesMatch()
    {
        // Arrange (each participant has its own context, so root subject IDs differ).
        var contextA = CreateContext();
        var rootA = CreateLeaf(contextA, "x", 1);

        var contextB = CreateContext();
        var rootB = CreateLeaf(contextB, "x", 1);

        // Act
        var snapshotA = SnapshotComparer.Capture(rootA);
        var snapshotB = SnapshotComparer.Capture(rootB);

        // Assert that per-participant root IDs are normalized to "ROOT".
        Assert.Equal(snapshotA, snapshotB);
        Assert.Contains("\"root\":\"ROOT\"", snapshotA);
        Assert.DoesNotContain("\"root\":null", snapshotA);
    }

    [Fact]
    public void WhenValueDiffers_ThenCapturesDiffer()
    {
        // Arrange
        var contextA = CreateContext();
        var rootA = CreateLeaf(contextA, "x", 1);

        var contextB = CreateContext();
        var rootB = CreateLeaf(contextB, "y", 1); // StringValue differs

        // Act
        var snapshotA = SnapshotComparer.Capture(rootA);
        var snapshotB = SnapshotComparer.Capture(rootB);

        // Assert
        Assert.NotEqual(snapshotA, snapshotB);
    }

    [Fact]
    public async Task WhenStructuralPropertyTimestampsDiffer_ThenCapturesMatch()
    {
        // Arrange: populate both graphs with structural state at different wall-clock times.
        var contextA = CreateContext();
        var rootA = new TestNode(contextA)
        {
            StringValue = "x",
            IntValue = 1,
            DecimalValue = 1,
            LongValue = 1,
            Collection = [CreateLeaf(contextA, "child", 1)],
            Items = new Dictionary<string, TestNode> { ["a"] = CreateLeaf(contextA, "a", 1) }
        };

        // Force a wall-clock gap so structural timestamps would differ if not stripped.
        await Task.Delay(20);

        var contextB = CreateContext();
        var rootB = new TestNode(contextB)
        {
            StringValue = "x",
            IntValue = 1,
            DecimalValue = 1,
            LongValue = 1,
            Collection = [CreateLeaf(contextB, "child", 1)],
            Items = new Dictionary<string, TestNode> { ["a"] = CreateLeaf(contextB, "a", 1) }
        };

        // Act
        var snapshotA = SnapshotComparer.Capture(rootA);
        var snapshotB = SnapshotComparer.Capture(rootB);

        // Assert
        Assert.Equal(snapshotA, snapshotB);
    }

    [Fact]
    public void WhenDictionaryItemOrderDiffers_ThenCapturesMatch()
    {
        // Arrange: same key/value pairs, different insertion order.
        var contextA = CreateContext();
        var rootA = new TestNode(contextA)
        {
            StringValue = "x", IntValue = 1, DecimalValue = 1, LongValue = 1,
            Items = new Dictionary<string, TestNode>
            {
                ["alpha"] = CreateLeaf(contextA, "a", 1),
                ["bravo"] = CreateLeaf(contextA, "b", 2)
            }
        };

        var contextB = CreateContext();
        var rootB = new TestNode(contextB)
        {
            StringValue = "x", IntValue = 1, DecimalValue = 1, LongValue = 1,
            Items = new Dictionary<string, TestNode>
            {
                ["bravo"] = CreateLeaf(contextB, "b", 2),
                ["alpha"] = CreateLeaf(contextB, "a", 1)
            }
        };

        // Act
        var snapshotA = SnapshotComparer.Capture(rootA);
        var snapshotB = SnapshotComparer.Capture(rootB);

        // Assert
        Assert.Equal(snapshotA, snapshotB);
    }

    [Fact]
    public void WhenCollectionItemOrderDiffers_ThenCapturesDiffer()
    {
        // Arrange: same items, different order.
        var contextA = CreateContext();
        var rootA = new TestNode(contextA)
        {
            StringValue = "x", IntValue = 1, DecimalValue = 1, LongValue = 1,
            Collection =
            [
                CreateLeaf(contextA, "a", 1),
                CreateLeaf(contextA, "b", 2)
            ]
        };

        var contextB = CreateContext();
        var rootB = new TestNode(contextB)
        {
            StringValue = "x", IntValue = 1, DecimalValue = 1, LongValue = 1,
            Collection =
            [
                CreateLeaf(contextB, "b", 2),
                CreateLeaf(contextB, "a", 1)
            ]
        };

        // Act
        var snapshotA = SnapshotComparer.Capture(rootA);
        var snapshotB = SnapshotComparer.Capture(rootB);

        // Assert
        Assert.NotEqual(snapshotA, snapshotB);
    }

    [Fact]
    public void WhenGraphHasCycle_ThenCaptureIsStable()
    {
        // Arrange: a -> b -> a cycle via ObjectRef.
        var context = CreateContext();
        var leafB = CreateLeaf(context, "b", 2);
        var rootA = new TestNode(context)
        {
            StringValue = "a", IntValue = 1, DecimalValue = 1, LongValue = 1,
            ObjectRef = leafB
        };
        leafB.ObjectRef = rootA;

        // Act: capture twice; deterministic output should match.
        var snapshot1 = SnapshotComparer.Capture(rootA);
        var snapshot2 = SnapshotComparer.Capture(rootA);

        // Assert
        Assert.Equal(snapshot1, snapshot2);
    }
}
