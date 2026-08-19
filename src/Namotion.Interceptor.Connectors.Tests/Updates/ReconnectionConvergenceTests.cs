using System.Reactive.Concurrency;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.Connectors.Updates.Internal;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Tests.Updates;

/// <summary>
/// Tests that simulate the reconnection scenario:
/// After a WebSocket disconnection, clients receive a welcome (complete state)
/// followed by buffered diffs. These tests verify that the complete-state
/// diff→apply round-trip produces convergent state on all participants.
/// </summary>
public class ReconnectionConvergenceTests
{
    /// <summary>
    /// Scenario: Two items swap positions. This is the exact pattern from the issue:
    /// "exactly two items swapped in a single collection"
    /// 1. Server has [A, X, Y, B]
    /// 2. Welcome is built
    /// 3. Stale write swaps X and Y on the server → [A, Y, X, B]
    /// 4. Diff is generated
    /// 5. Client applies Welcome [A, X, Y, B], then diff
    /// 6. Expected: client = [A, Y, X, B]
    /// </summary>
    [Fact]
    public void WhenTwoItemsAreSwappedAfterWelcome_ThenClientConvergesWithServer()
    {
        // Arrange
        var serverContext = InterceptorSubjectContext.Create()
            .WithPropertyChangeSubscriptions()
            .WithRegistry();
        var childA = new CycleTestNode { Name = "A" };
        var childX = new CycleTestNode { Name = "X" };
        var childY = new CycleTestNode { Name = "Y" };
        var childB = new CycleTestNode { Name = "B" };
        var server = new CycleTestNode(serverContext)
        {
            Name = "Server",
            Items = [childA, childX, childY, childB]
        };

        childX.GetOrAddSubjectId();
        childY.GetOrAddSubjectId();
        childA.GetOrAddSubjectId();
        server.GetOrAddSubjectId();

        // Build Welcome
        var welcome = SubjectUpdate.CreateCompleteUpdate(server, []);

        // Capture changes
        var changes = new List<SubjectPropertyChange>();
        using var subscription = serverContext.GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(c => changes.Add(c));

        // Act - swap X and Y on the server, then replay Welcome plus the resulting diff on a client
        server.Items = [childA, childY, childX, childB];
        var diff = SubjectUpdate.CreatePartialUpdateFromChanges(server, changes.ToArray(), []);

        var clientContext = InterceptorSubjectContext.Create().WithRegistry();
        var client = new CycleTestNode(clientContext);

        client.ApplySubjectUpdate(welcome, DefaultSubjectFactory.Instance, ChangeOrigin.Local);
        var clientAfterWelcome = client.Items.Select(item => item.Name).ToArray();

        client.ApplySubjectUpdate(diff, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert - Welcome is the pre-swap baseline and the diff converges the client on the server
        Assert.Equal(new[] { "A", "X", "Y", "B" }, clientAfterWelcome);
        Assert.Equal(new[] { "A", "Y", "X", "B" }, server.Items.Select(item => item.Name).ToArray());
        AssertSameOrdering(server, client, "after diff");
    }

    /// <summary>
    /// Scenario: Stale Move applied via collection reassignment,
    /// followed by a second unrelated change in the same batch window.
    /// Tests the deduplication path where MergeWithNewer combines two collection changes.
    /// 1. Server has [A, B, C]
    /// 2. Welcome is built
    /// 3. Move C to head → [C, A, B]
    /// 4. Second change in same batch: Insert D after A → [C, A, D, B]
    /// 5. Changes are deduped (oldest old + newest new), diff generated
    /// 6. Client applies Welcome [A, B, C], then diff
    /// 7. Expected: client = [C, A, D, B]
    /// </summary>
    [Fact]
    public void WhenBatchedMoveAndInsertFollowWelcome_ThenClientConvergesWithServer()
    {
        // Arrange
        var serverContext = InterceptorSubjectContext.Create()
            .WithPropertyChangeSubscriptions()
            .WithRegistry();
        var childA = new CycleTestNode { Name = "A" };
        var childB = new CycleTestNode { Name = "B" };
        var childC = new CycleTestNode { Name = "C" };
        var server = new CycleTestNode(serverContext)
        {
            Name = "Server",
            Items = [childA, childB, childC]
        };

        server.GetOrAddSubjectId();
        childA.GetOrAddSubjectId();
        childB.GetOrAddSubjectId();
        childC.GetOrAddSubjectId();

        // Build Welcome
        var welcome = SubjectUpdate.CreateCompleteUpdate(server, []);

        // Capture ALL changes (simulates what the ChangeQueueProcessor collects)
        var allChanges = new List<SubjectPropertyChange>();
        using var subscription = serverContext.GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(c => allChanges.Add(c));

        // Act - change 1: move C to head, change 2: insert D after A, both in the same batch window
        server.Items = [childC, childA, childB];
        var childD = new CycleTestNode { Name = "D" };
        server.Items = [childC, childA, childD, childB];

        // Simulate ChangeQueueProcessor deduplication:
        // Two changes to same Items property, merged as oldest old plus newest new
        var deduped = DeduplicateChanges(allChanges);
        var diff = SubjectUpdate.CreatePartialUpdateFromChanges(server, deduped, []);

        var clientContext = InterceptorSubjectContext.Create().WithRegistry();
        var client = new CycleTestNode(clientContext);

        client.ApplySubjectUpdate(welcome, DefaultSubjectFactory.Instance, ChangeOrigin.Local);
        var clientAfterWelcome = client.Items.Select(item => item.Name).ToArray();

        client.ApplySubjectUpdate(diff, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert - Welcome is the pre-change baseline and the merged diff converges the client
        Assert.Equal(new[] { "A", "B", "C" }, clientAfterWelcome);
        Assert.Equal(new[] { "C", "A", "D", "B" }, server.Items.Select(item => item.Name).ToArray());
        AssertSameOrdering(server, client, "after merged diff");
    }

    /// <summary>
    /// Simulates the ChangeQueueProcessor deduplication: for multiple changes to the
    /// same property, keeps the oldest old value and the newest new value.
    /// </summary>
    private static SubjectPropertyChange[] DeduplicateChanges(List<SubjectPropertyChange> changes)
    {
        var byProperty = new Dictionary<PropertyReference, SubjectPropertyChange>();
        foreach (var change in changes)
        {
            if (byProperty.TryGetValue(change.Property, out var existing))
            {
                // Keep oldest old (existing) + newest new (change)
                byProperty[change.Property] = existing.MergeWithNewer(change);
            }
            else
            {
                byProperty[change.Property] = change;
            }
        }
        return byProperty.Values.ToArray();
    }

    private static void AssertSameOrdering(CycleTestNode expected, CycleTestNode actual, string label)
    {
        Assert.Equal(expected.Items.Count, actual.Items.Count);
        for (var i = 0; i < expected.Items.Count; i++)
        {
            Assert.True(
                expected.Items[i].Name == actual.Items[i].Name,
                $"{label}: Index {i} mismatch. Expected '{expected.Items[i].Name}', got '{actual.Items[i].Name}'. " +
                $"Expected order: [{string.Join(", ", expected.Items.Select(x => x.Name))}], " +
                $"Actual order: [{string.Join(", ", actual.Items.Select(x => x.Name))}]");
        }
    }
}
