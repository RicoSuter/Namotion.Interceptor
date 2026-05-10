using System.Text.Json.Nodes;
using Xunit;
using Namotion.Interceptor.ConnectorTester.Engine;
using Namotion.Interceptor.ConnectorTester.Model;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.ConnectorTester.Tests.Engine;

public class SnapshotComparerTests
{
    // Tests use a fixed timestamp so Value-property write timestamps converge across
    // independently-constructed contexts. SnapshotComparer.Capture preserves Value
    // timestamps by design (they propagate via OPC UA source timestamps in production
    // and must match for convergence). Without the fixed timestamp, two contexts
    // created at different wall-clock times would produce snapshots that differ on
    // every Value timestamp field.
    private static readonly DateTimeOffset FixedTimestamp =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

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
        using var _ = SubjectChangeContext.WithChangedTimestamp(FixedTimestamp);

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
        using var _ = SubjectChangeContext.WithChangedTimestamp(FixedTimestamp);

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
        using var _ = SubjectChangeContext.WithChangedTimestamp(FixedTimestamp);

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
    public void WhenGraphHasStructuralProperties_ThenStructuralTimestampsAreStrippedFromCapture()
    {
        // Arrange: Collection, Dictionary, and Object kinds are local creation moments
        // and are stripped during normalization. Verify their absence by inspecting JSON.
        using var _ = SubjectChangeContext.WithChangedTimestamp(FixedTimestamp);

        var context = CreateContext();
        var root = new TestNode(context)
        {
            StringValue = "x",
            IntValue = 1,
            DecimalValue = 1,
            LongValue = 1,
            ObjectRef = CreateLeaf(context, "ref", 0),
            Collection = [CreateLeaf(context, "child", 1)],
            Items = new Dictionary<string, TestNode> { ["a"] = CreateLeaf(context, "a", 1) }
        };

        // Act
        var snapshot = SnapshotComparer.Capture(root);
        var subjects = JsonNode.Parse(snapshot)!["subjects"]!.AsObject();

        // Assert: Collection, Dictionary, and Object kinds carry no "timestamp" field.
        // Value kinds may carry a timestamp (intentionally preserved for convergence checks).
        var sawStructural = false;
        foreach (var (_, subjectNode) in subjects)
        {
            foreach (var (propertyName, propertyNode) in subjectNode!.AsObject())
            {
                var property = propertyNode!.AsObject();
                var kind = property["kind"]?.GetValue<string>();
                if (kind is "Collection" or "Dictionary" or "Object")
                {
                    sawStructural = true;
                    Assert.False(
                        property.ContainsKey("timestamp"),
                        $"Structural property '{propertyName}' (kind={kind}) must not include a timestamp.");
                }
            }
        }
        Assert.True(sawStructural, "Test graph must include at least one structural property.");
    }

    [Fact]
    public void WhenDictionaryItemOrderDiffers_ThenCapturesMatch()
    {
        // Arrange: same key/value pairs, different insertion order.
        using var _ = SubjectChangeContext.WithChangedTimestamp(FixedTimestamp);

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
        using var _ = SubjectChangeContext.WithChangedTimestamp(FixedTimestamp);

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
        using var _ = SubjectChangeContext.WithChangedTimestamp(FixedTimestamp);

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
