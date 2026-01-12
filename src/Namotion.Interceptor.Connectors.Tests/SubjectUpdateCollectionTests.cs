using System.Reactive.Concurrency;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Tests;

/// <summary>
/// Tests for collection (array/list) diff operations in SubjectUpdate.
/// Tests Insert, Remove, Move operations and sparse property updates.
/// </summary>
public class SubjectUpdateCollectionTests
{
    [Fact]
    public async Task WhenItemInsertedAtEnd_ThenInsertOperationIsCreated()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeObservable().WithRegistry();
        var child1 = new CycleTestNode { Name = "Child1" };
        var node = new CycleTestNode(context) { Name = "Root", Items = [child1] };

        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(c => changes.Add(c));

        // Act - insert new item
        var child2 = new CycleTestNode { Name = "Child2" };
        node.Items = [child1, child2];

        var update = SubjectUpdate.CreatePartialUpdateFromChanges(node, changes.ToArray(), []);

        // Assert
        await Verify(update);
    }

    [Fact]
    public async Task WhenItemInsertedAtBeginning_ThenInsertOperationIsCreated()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeObservable().WithRegistry();
        var child2 = new CycleTestNode { Name = "Child2" };
        var node = new CycleTestNode(context) { Name = "Root", Items = [child2] };

        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(c => changes.Add(c));

        // Act - insert at beginning
        var child1 = new CycleTestNode { Name = "Child1" };
        node.Items = [child1, child2];

        var update = SubjectUpdate.CreatePartialUpdateFromChanges(node, changes.ToArray(), []);

        // Assert
        await Verify(update);
    }

    [Fact]
    public async Task WhenItemRemoved_ThenRemoveOperationIsCreated()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeObservable().WithRegistry();
        var child1 = new CycleTestNode { Name = "Child1" };
        var child2 = new CycleTestNode { Name = "Child2" };
        var child3 = new CycleTestNode { Name = "Child3" };
        var node = new CycleTestNode(context) { Name = "Root", Items = [child1, child2, child3] };

        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(c => changes.Add(c));

        // Act - remove middle item
        node.Items = [child1, child3];

        var update = SubjectUpdate.CreatePartialUpdateFromChanges(node, changes.ToArray(), []);

        // Assert
        await Verify(update);
    }

    [Fact]
    public async Task WhenItemMoved_ThenMoveOperationWithoutItemDataIsCreated()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeObservable().WithRegistry();
        var child1 = new CycleTestNode { Name = "Child1" };
        var child2 = new CycleTestNode { Name = "Child2" };
        var child3 = new CycleTestNode { Name = "Child3" };
        var node = new CycleTestNode(context) { Name = "Root", Items = [child1, child2, child3] };

        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(c => changes.Add(c));

        // Act - reorder: move child3 to first position
        node.Items = [child3, child1, child2];

        var update = SubjectUpdate.CreatePartialUpdateFromChanges(node, changes.ToArray(), []);

        // Assert - Move should have NO Item data, just FromIndex and Index
        await Verify(update);
    }

    [Fact]
    public async Task WhenItemPropertyChanged_ThenSparseUpdateByFinalIndexIsCreated()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeObservable().WithRegistry();
        var child1 = new CycleTestNode { Name = "Child1" };
        var child2 = new CycleTestNode { Name = "Child2" };
        var child3 = new CycleTestNode { Name = "Child3" };
        var node = new CycleTestNode(context) { Name = "Root", Items = [child1, child2, child3] };

        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(c => changes.Add(c));

        // Act - change property of middle item (no structural change)
        child2.Name = "Child2Updated";

        var update = SubjectUpdate.CreatePartialUpdateFromChanges(node, changes.ToArray(), []);

        // Assert - should have Collection update at index 1, not Operations
        await Verify(update);
    }

    [Fact]
    public async Task WhenMultipleItemsHavePropertyChanges_ThenSparseUpdatesAreCreated()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeObservable().WithRegistry();
        var child1 = new CycleTestNode { Name = "Child1" };
        var child2 = new CycleTestNode { Name = "Child2" };
        var child3 = new CycleTestNode { Name = "Child3" };
        var node = new CycleTestNode(context) { Name = "Root", Items = [child1, child2, child3] };

        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(c => changes.Add(c));

        // Act - change properties of first and last items (sparse updates)
        child1.Name = "Child1Updated";
        child3.Name = "Child3Updated";

        var update = SubjectUpdate.CreatePartialUpdateFromChanges(node, changes.ToArray(), []);

        // Assert - should have Collection updates at indices 0 and 2 only
        await Verify(update);
    }

    /// <summary>
    /// Regression test for BuildPathToRoot bug where only the first item's path was included
    /// when multiple items in the same collection had property changes in the same batch.
    /// Previously, after the first item was processed, subsequent items were skipped because
    /// the parent property already existed in the update - but we should APPEND to the
    /// collection update, not skip entirely.
    /// </summary>
    [Fact]
    public void WhenManyCollectionItemsHavePropertyChanges_ThenAllAreReferencedInParentCollection()
    {
        // Arrange - create a collection with many items (simulates the benchmark scenario)
        var context = InterceptorSubjectContext.Create().WithPropertyChangeObservable().WithRegistry();
        var items = Enumerable.Range(0, 100).Select(i => new CycleTestNode { Name = $"Child{i}" }).ToList();
        var node = new CycleTestNode(context) { Name = "Root", Items = items };

        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(c => changes.Add(c));

        // Act - update ALL items' properties in a single batch (this is what the benchmark does)
        for (var i = 0; i < items.Count; i++)
        {
            items[i].Name = $"Child{i}Updated";
        }

        var update = SubjectUpdate.CreatePartialUpdateFromChanges(node, changes.ToArray(), []);

        // Assert - ALL 100 items should be referenced in the parent's collection update
        Assert.NotNull(update.Subjects);
        Assert.True(update.Subjects.TryGetValue(update.Root!, out var rootProperties));
        Assert.True(rootProperties!.TryGetValue("Items", out var itemsUpdate));
        Assert.Equal(SubjectPropertyUpdateKind.Collection, itemsUpdate!.Kind);
        Assert.NotNull(itemsUpdate.Collection);

        // This was the bug: only 1 item was included instead of all 100
        Assert.Equal(100, itemsUpdate.Collection.Count);

        // Verify all indices are present
        var indices = itemsUpdate.Collection.Select(c => (int)c.Index!).OrderBy(i => i).ToList();
        Assert.Equal(Enumerable.Range(0, 100).ToList(), indices);

        // Verify all items have their property updates
        Assert.Equal(101, update.Subjects.Count); // 1 root + 100 items
    }

    [Fact]
    public async Task WhenRemoveAndInsertCombined_ThenBothOperationsAreCreated()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeObservable().WithRegistry();
        var child1 = new CycleTestNode { Name = "Child1" };
        var child2 = new CycleTestNode { Name = "Child2" };
        var node = new CycleTestNode(context) { Name = "Root", Items = [child1, child2] };

        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(c => changes.Add(c));

        // Act - remove child1, add child3
        var child3 = new CycleTestNode { Name = "Child3" };
        node.Items = [child2, child3];

        var update = SubjectUpdate.CreatePartialUpdateFromChanges(node, changes.ToArray(), []);

        // Assert
        await Verify(update);
    }

    [Fact]
    public async Task WhenMoveWithPropertyUpdate_ThenMoveOperationAndSparseUpdateAreCreated()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeObservable().WithRegistry();
        var child1 = new CycleTestNode { Name = "Child1" };
        var child2 = new CycleTestNode { Name = "Child2" };
        var child3 = new CycleTestNode { Name = "Child3" };
        var node = new CycleTestNode(context) { Name = "Root", Items = [child1, child2, child3] };

        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(c => changes.Add(c));

        // Act - change property AND reorder
        child2.Name = "Child2Updated";
        node.Items = [child3, child1, child2]; // move child3 to front

        var update = SubjectUpdate.CreatePartialUpdateFromChanges(node, changes.ToArray(), []);

        // Assert - should have Move operations AND property update for child2
        await Verify(update);
    }

    [Fact]
    public async Task WhenInsertWithPropertyUpdateOnExisting_ThenBothTypesAreCreated()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeObservable().WithRegistry();
        var child1 = new CycleTestNode { Name = "Child1" };
        var node = new CycleTestNode(context) { Name = "Root", Items = [child1] };

        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(c => changes.Add(c));

        // Act - update existing item AND insert new item
        child1.Name = "Child1Updated";
        var child2 = new CycleTestNode { Name = "Child2" };
        node.Items = [child1, child2];

        var update = SubjectUpdate.CreatePartialUpdateFromChanges(node, changes.ToArray(), []);

        // Assert
        await Verify(update);
    }

    [Fact]
    public async Task WhenCollectionBecomesEmpty_ThenRemoveOperationsAreCreated()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeObservable().WithRegistry();
        var child1 = new CycleTestNode { Name = "Child1" };
        var child2 = new CycleTestNode { Name = "Child2" };
        var node = new CycleTestNode(context) { Name = "Root", Items = [child1, child2] };

        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(c => changes.Add(c));

        // Act - clear collection
        node.Items = [];

        var update = SubjectUpdate.CreatePartialUpdateFromChanges(node, changes.ToArray(), []);

        // Assert
        await Verify(update);
    }

    [Fact]
    public async Task WhenCollectionPopulatedFromEmpty_ThenInsertOperationsAreCreated()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeObservable().WithRegistry();
        var node = new CycleTestNode(context) { Name = "Root", Items = [] };

        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(c => changes.Add(c));

        // Act - populate from empty
        var child1 = new CycleTestNode { Name = "Child1" };
        var child2 = new CycleTestNode { Name = "Child2" };
        node.Items = [child1, child2];

        var update = SubjectUpdate.CreatePartialUpdateFromChanges(node, changes.ToArray(), []);

        // Assert
        await Verify(update);
    }

    [Fact]
    public async Task WhenCompleteCollectionUpdate_ThenAllItemsIncluded()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var child1 = new CycleTestNode { Name = "Child1" };
        var child2 = new CycleTestNode { Name = "Child2" };
        var node = new CycleTestNode(context) { Name = "Root", Items = [child1, child2] };

        // Act - create complete update (not partial)
        var update = SubjectUpdate.CreateCompleteUpdate(node, []);

        // Assert - Collection should have all items with full data
        await Verify(update);
    }
}
