using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.Connectors.Updates.Internal;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Connectors.Tests.Updates;

/// <summary>
/// Tests for the bounded cross-update pending-apply buffer that recovers
/// subject updates which were momentarily unresolvable at apply time instead
/// of silently dropping them.
/// </summary>
public class PendingApplyBufferTests
{
    private static Dictionary<string, SubjectPropertyUpdate> ValueUpdate(
        string propertyName, object? value, DateTimeOffset? timestamp)
    {
        return new Dictionary<string, SubjectPropertyUpdate>
        {
            [propertyName] = new SubjectPropertyUpdate
            {
                Kind = SubjectPropertyUpdateKind.Value,
                Value = value,
                Timestamp = timestamp
            }
        };
    }

    [Fact]
    public void WhenSubjectAdded_ThenBufferIsNotEmpty()
    {
        // Arrange
        var buffer = new PendingApplyBuffer();

        // Act
        buffer.Add("subject-1", ValueUpdate("Name", "A", DateTimeOffset.UtcNow));

        // Assert
        Assert.False(buffer.IsEmpty);
    }

    [Fact]
    public void WhenSameSubjectAddedWithNewerTimestamp_ThenNewerValueWins()
    {
        // Arrange
        var buffer = new PendingApplyBuffer();
        var older = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var newer = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);

        buffer.Add("subject-1", ValueUpdate("Name", "Old", older));

        // Act
        buffer.Add("subject-1", ValueUpdate("Name", "New", newer));

        // Assert: drain into a resolvable subject and verify the newer value applied
        var (context, node, _) = CreateContextWithResolvableSubject("subject-1");
        buffer.DrainResolvable(context);

        Assert.Equal("New", node.Name);
        Assert.True(buffer.IsEmpty);
    }

    [Fact]
    public void WhenSameSubjectAddedWithOlderTimestamp_ThenExistingValueWins()
    {
        // Arrange
        var buffer = new PendingApplyBuffer();
        var older = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var newer = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);

        buffer.Add("subject-1", ValueUpdate("Name", "New", newer));

        // Act: incoming has older timestamp, must not overwrite the existing newer value
        buffer.Add("subject-1", ValueUpdate("Name", "Old", older));

        // Assert
        var (context, node, _) = CreateContextWithResolvableSubject("subject-1");
        buffer.DrainResolvable(context);

        Assert.Equal("New", node.Name);
    }

    [Fact]
    public void WhenEvictionOccursOverMaxSubjects_ThenOldestDroppedAndCounterIncremented()
    {
        // Arrange
        var buffer = new PendingApplyBuffer();
        var before = SubjectUpdateApplier.DroppedSubjectUpdateCount;

        // Fill exactly to capacity
        for (var i = 0; i < PendingApplyBuffer.MaxSubjects; i++)
        {
            buffer.Add($"subject-{i}", ValueUpdate("Name", i, DateTimeOffset.UtcNow));
        }

        // Act: one more push beyond capacity must evict the oldest entry
        buffer.Add("overflow", ValueUpdate("Name", "overflow", DateTimeOffset.UtcNow));

        // Assert: eviction (genuine loss) counter advanced by exactly 1
        Assert.Equal(before + 1, SubjectUpdateApplier.DroppedSubjectUpdateCount);

        // The oldest entry (subject-0) was dropped; draining it into a resolvable
        // subject must NOT apply (it is no longer pending).
        var (context, node, _) = CreateContextWithResolvableSubject("subject-0");
        buffer.DrainResolvable(context);
        Assert.Null(node.Name); // oldest entry was evicted, so nothing applied
    }

    [Fact]
    public void WhenDrainResolvable_ThenResolvableAppliedAndRemovedUnresolvableLeftPending()
    {
        // Arrange: registry contains "resolvable" but not "missing"
        var (context, node, _) = CreateContextWithResolvableSubject("resolvable");

        var buffer = new PendingApplyBuffer();
        buffer.Add("resolvable", ValueUpdate("Name", "Applied", DateTimeOffset.UtcNow));
        buffer.Add("missing", ValueUpdate("Name", "Pending", DateTimeOffset.UtcNow));

        var recoveredBefore = PendingApplyBuffer.RecoveredSubjectUpdateCount;

        // Act
        buffer.DrainResolvable(context);

        // Assert: resolvable applied and removed, missing remains pending
        Assert.Equal("Applied", node.Name);
        Assert.False(buffer.IsEmpty); // "missing" still pending
        Assert.Equal(recoveredBefore + 1, PendingApplyBuffer.RecoveredSubjectUpdateCount);

        // Draining again with "missing" now resolvable applies it and empties the buffer
        var (context2, node2, _) = CreateContextWithResolvableSubject("missing");
        buffer.DrainResolvable(context2);
        Assert.Equal("Pending", node2.Name);
        Assert.True(buffer.IsEmpty);
    }

    /// <summary>
    /// Builds a real apply context whose registry resolves <paramref name="subjectId"/>
    /// to a fresh <see cref="CycleTestNode"/>. The context is initialized with an empty
    /// Subjects map (the buffer carries the property updates, not the context).
    /// </summary>
    private static (SubjectUpdateApplyContext context, CycleTestNode node, string subjectId)
        CreateContextWithResolvableSubject(string subjectId)
    {
        var rootContext = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var root = new CycleTestNode(rootContext) { Name = "Root" };
        var node = new CycleTestNode { Name = null };
        root.Child = node;
        node.SetSubjectId(subjectId);

        var context = new SubjectUpdateApplyContext();
        context.Initialize(
            ((IInterceptorSubject)root).Context,
            new Dictionary<string, Dictionary<string, SubjectPropertyUpdate>>(),
            completeSubjectIds: null,
            new DefaultSubjectFactory(),
            transformValueBeforeApply: null);

        return (context, node, subjectId);
    }
}
