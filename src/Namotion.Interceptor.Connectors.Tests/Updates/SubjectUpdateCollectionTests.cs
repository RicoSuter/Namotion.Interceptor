using System.Reactive.Concurrency;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Tests.Updates;

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
        Assert.NotNull(itemsUpdate.Items);

        // This was the bug: only 1 item was included instead of all 100
        Assert.Equal(100, itemsUpdate.Items.Count);

        // Verify all indices are present
        var indices = itemsUpdate.Items.Select(c => (int)c.Index!).OrderBy(i => i).ToList();
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

    [Fact]
    public async Task WhenEmptyCollectionRemainsEmpty_ThenNoOperationsAreCreated()
    {
        // Arrange - both old and new collections are empty
        var context = InterceptorSubjectContext.Create().WithPropertyChangeObservable().WithRegistry();
        var node = new CycleTestNode(context) { Name = "Root", Items = [] };

        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(c => changes.Add(c));

        // Act - assign a new empty collection (triggers change detection)
        node.Items = [];

        var update = SubjectUpdate.CreatePartialUpdateFromChanges(node, changes.ToArray(), []);

        // Assert - no operations should be created for empty-to-empty
        await Verify(update);
    }

    [Fact]
    public async Task WhenSingleItemCollectionPropertyChanges_ThenSparseUpdateIsCreated()
    {
        // Arrange - collection with exactly one item
        var context = InterceptorSubjectContext.Create().WithPropertyChangeObservable().WithRegistry();
        var child = new CycleTestNode { Name = "OnlyChild" };
        var node = new CycleTestNode(context) { Name = "Root", Items = [child] };

        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(c => changes.Add(c));

        // Act - change the single item's property
        child.Name = "UpdatedChild";

        var update = SubjectUpdate.CreatePartialUpdateFromChanges(node, changes.ToArray(), []);

        // Assert - sparse update at index 0
        await Verify(update);
    }

    [Fact]
    public async Task WhenSingleItemRemoved_ThenRemoveOperationAtIndexZero()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeObservable().WithRegistry();
        var child = new CycleTestNode { Name = "OnlyChild" };
        var node = new CycleTestNode(context) { Name = "Root", Items = [child] };

        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(c => changes.Add(c));

        // Act - remove the only item
        node.Items = [];

        var update = SubjectUpdate.CreatePartialUpdateFromChanges(node, changes.ToArray(), []);

        // Assert - Remove at index 0
        await Verify(update);
    }

    [Fact]
    public async Task WhenFirstItemPropertyChanges_ThenSparseUpdateAtIndexZero()
    {
        // Arrange - test boundary condition at index 0
        var context = InterceptorSubjectContext.Create().WithPropertyChangeObservable().WithRegistry();
        var child1 = new CycleTestNode { Name = "First" };
        var child2 = new CycleTestNode { Name = "Second" };
        var node = new CycleTestNode(context) { Name = "Root", Items = [child1, child2] };

        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(c => changes.Add(c));

        // Act - change first item (index 0)
        child1.Name = "FirstUpdated";

        var update = SubjectUpdate.CreatePartialUpdateFromChanges(node, changes.ToArray(), []);

        // Assert - sparse update at index 0
        await Verify(update);
    }

    [Fact]
    public async Task WhenFirstItemRemoved_ThenRemoveAtIndexZero()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeObservable().WithRegistry();
        var child1 = new CycleTestNode { Name = "First" };
        var child2 = new CycleTestNode { Name = "Second" };
        var node = new CycleTestNode(context) { Name = "Root", Items = [child1, child2] };

        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(c => changes.Add(c));

        // Act - remove first item
        node.Items = [child2];

        var update = SubjectUpdate.CreatePartialUpdateFromChanges(node, changes.ToArray(), []);

        // Assert
        await Verify(update);
    }

    [Fact]
    public async Task WhenInsertAtIndexZero_ThenInsertOperationAtZero()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeObservable().WithRegistry();
        var child1 = new CycleTestNode { Name = "Existing" };
        var node = new CycleTestNode(context) { Name = "Root", Items = [child1] };

        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(c => changes.Add(c));

        // Act - insert at index 0
        var newChild = new CycleTestNode { Name = "NewFirst" };
        node.Items = [newChild, child1];

        var update = SubjectUpdate.CreatePartialUpdateFromChanges(node, changes.ToArray(), []);

        // Assert
        await Verify(update);
    }

    [Fact]
    public void WhenRemoveAndMoveCombined_ThenMoveIndicesAccountForRemovals()
    {
        // Arrange: [A, B, C] where we'll remove A and reorder B,C to C,B
        var context = InterceptorSubjectContext.Create().WithPropertyChangeObservable().WithRegistry();
        var childA = new CycleTestNode { Name = "ChildA" };
        var childB = new CycleTestNode { Name = "ChildB" };
        var childC = new CycleTestNode { Name = "ChildC" };
        var node = new CycleTestNode(context) { Name = "Root", Items = [childA, childB, childC] };

        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(c => changes.Add(c));

        // Act - remove A and reorder to [C, B]
        node.Items = [childC, childB];

        var update = SubjectUpdate.CreatePartialUpdateFromChanges(node, changes.ToArray(), []);

        // Assert - operations should use intermediate indices
        Assert.NotNull(update.Subjects);
        Assert.True(update.Subjects.TryGetValue(update.Root!, out var rootProperties));
        Assert.True(rootProperties!.TryGetValue("Items", out var itemsUpdate));
        Assert.NotNull(itemsUpdate!.Operations);

        var removes = itemsUpdate.Operations.Where(op => op.Action == SubjectCollectionOperationType.Remove).ToList();
        var moves = itemsUpdate.Operations.Where(op => op.Action == SubjectCollectionOperationType.Move).ToList();

        Assert.Single(removes);
        Assert.Equal(0, removes[0].Index); // A was at index 0

        // Key assertion: Move indices must be valid AFTER the remove
        // After removing A at index 0, array is [B, C] with indices [0, 1]
        // Move indices should reference this intermediate state
        foreach (var move in moves)
        {
            var fromIndex = move.FromIndex!.Value;
            // After remove, max valid index is 1 (array has 2 items)
            Assert.True(fromIndex <= 1, $"Move fromIndex {fromIndex} should be <= 1 after remove");
        }
    }

    [Fact]
    public void WhenRemoveAndMoveCombined_ThenApplyProducesCorrectResult()
    {
        // Arrange: Create source with [A, B, C]
        var sourceContext = InterceptorSubjectContext.Create().WithPropertyChangeObservable().WithRegistry();
        var childA = new CycleTestNode { Name = "ChildA" };
        var childB = new CycleTestNode { Name = "ChildB" };
        var childC = new CycleTestNode { Name = "ChildC" };
        var source = new CycleTestNode(sourceContext) { Name = "Root", Items = [childA, childB, childC] };

        var changes = new List<SubjectPropertyChange>();
        sourceContext.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(c => changes.Add(c));

        // Act - remove A and reorder to [C, B]
        source.Items = [childC, childB];
        var update = SubjectUpdate.CreatePartialUpdateFromChanges(source, changes.ToArray(), []);

        // Create target with same initial state
        var targetContext = InterceptorSubjectContext.Create().WithRegistry();
        var targetA = new CycleTestNode { Name = "ChildA" };
        var targetB = new CycleTestNode { Name = "ChildB" };
        var targetC = new CycleTestNode { Name = "ChildC" };
        var target = new CycleTestNode(targetContext) { Name = "Root", Items = [targetA, targetB, targetC] };

        // Apply update
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance);

        // Assert - target should have [C, B] (by name, since these are different instances)
        Assert.Equal(2, target.Items.Count);
        Assert.Equal("ChildC", target.Items[0].Name);
        Assert.Equal("ChildB", target.Items[1].Name);
    }

    [Fact]
    public void WhenMultipleRemovesAndMove_ThenIndicesAccountForAllRemovals()
    {
        // Arrange: [A, B, C, D, E] -> remove A and C, reorder remaining to [E, B, D]
        var context = InterceptorSubjectContext.Create().WithPropertyChangeObservable().WithRegistry();
        var childA = new CycleTestNode { Name = "A" };
        var childB = new CycleTestNode { Name = "B" };
        var childC = new CycleTestNode { Name = "C" };
        var childD = new CycleTestNode { Name = "D" };
        var childE = new CycleTestNode { Name = "E" };
        var node = new CycleTestNode(context) { Name = "Root", Items = [childA, childB, childC, childD, childE] };

        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(c => changes.Add(c));

        // Act - remove A, C and reorder to [E, B, D]
        node.Items = [childE, childB, childD];

        var update = SubjectUpdate.CreatePartialUpdateFromChanges(node, changes.ToArray(), []);

        // Create target and apply
        var targetContext = InterceptorSubjectContext.Create().WithRegistry();
        var target = new CycleTestNode(targetContext)
        {
            Name = "Root",
            Items = [
                new CycleTestNode { Name = "A" },
                new CycleTestNode { Name = "B" },
                new CycleTestNode { Name = "C" },
                new CycleTestNode { Name = "D" },
                new CycleTestNode { Name = "E" }
            ]
        };

        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance);

        // Assert
        Assert.Equal(3, target.Items.Count);
        Assert.Equal("E", target.Items[0].Name);
        Assert.Equal("B", target.Items[1].Name);
        Assert.Equal("D", target.Items[2].Name);
    }

    [Fact]
    public async Task WhenCollectionSetToNull_ThenCompleteUpdateHasValueKindWithNull()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var node = new CycleTestNode(context) { Name = "Root", Items = null! };

        // Act
        var update = SubjectUpdate.CreateCompleteUpdate(node, []);

        // Assert - Items should be Value kind with null, not Collection kind
        Assert.NotNull(update.Subjects);
        Assert.True(update.Subjects.TryGetValue(update.Root!, out var rootProperties));
        Assert.True(rootProperties!.TryGetValue("Items", out var itemsUpdate));
        Assert.Equal(SubjectPropertyUpdateKind.Value, itemsUpdate!.Kind);
        Assert.Null(itemsUpdate.Value);

        await Verify(update);
    }

    [Fact]
    public void WhenCollectionSetToNull_ThenPartialUpdateHasValueKindWithNull()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeObservable().WithRegistry();
        var child1 = new CycleTestNode { Name = "Child1" };
        var node = new CycleTestNode(context) { Name = "Root", Items = [child1] };

        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(c => changes.Add(c));

        // Act - set collection to null
        node.Items = null!;

        var update = SubjectUpdate.CreatePartialUpdateFromChanges(node, changes.ToArray(), []);

        // Assert - Items should be Value kind with null
        Assert.NotNull(update.Subjects);
        Assert.True(update.Subjects.TryGetValue(update.Root!, out var rootProperties));
        Assert.True(rootProperties!.TryGetValue("Items", out var itemsUpdate));
        Assert.Equal(SubjectPropertyUpdateKind.Value, itemsUpdate!.Kind);
        Assert.Null(itemsUpdate.Value);
    }

    [Fact]
    public void WhenNullCollectionApplied_ThenTargetCollectionBecomesNull()
    {
        // Arrange - create source with null collection
        var sourceContext = InterceptorSubjectContext.Create().WithPropertyChangeObservable().WithRegistry();
        var child1 = new CycleTestNode { Name = "Child1" };
        var source = new CycleTestNode(sourceContext) { Name = "Root", Items = [child1] };

        var changes = new List<SubjectPropertyChange>();
        sourceContext.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(c => changes.Add(c));

        source.Items = null!;
        var update = SubjectUpdate.CreatePartialUpdateFromChanges(source, changes.ToArray(), []);

        // Create target with populated collection
        var targetContext = InterceptorSubjectContext.Create().WithRegistry();
        var target = new CycleTestNode(targetContext) { Name = "Root", Items = [new CycleTestNode { Name = "Child1" }] };

        // Act
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance);

        // Assert
        Assert.Null(target.Items);
    }

    /// <summary>
    /// Regression test: When a complete update (e.g., Welcome after reconnect) declares fewer
    /// items than the target currently has, the excess items must be trimmed.
    /// Bug scenario: Server reconnects with a new ObjectRef whose Collection has 1 item,
    /// but the client still has the old ObjectRef with 3 items. Without trimming, the
    /// client would keep the stale extra items.
    /// </summary>
    [Fact]
    public void WhenCompleteUpdateHasFewerItems_ThenTargetCollectionIsTrimmed()
    {
        // Arrange - source has 1 item
        var sourceContext = InterceptorSubjectContext.Create().WithRegistry();
        var sourceChild = new CycleTestNode(sourceContext) { Name = "SourceChild1" };
        var source = new CycleTestNode(sourceContext) { Name = "Root", Items = [sourceChild] };

        var update = SubjectUpdate.CreateCompleteUpdate(source, []);

        // Arrange - target has 3 items (stale state from before reconnect)
        var targetContext = InterceptorSubjectContext.Create().WithRegistry();
        var target = new CycleTestNode(targetContext)
        {
            Name = "Root",
            Items =
            [
                new CycleTestNode { Name = "StaleChild1" },
                new CycleTestNode { Name = "StaleChild2" },
                new CycleTestNode { Name = "StaleChild3" }
            ]
        };

        // Act
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance);

        // Assert - target should have exactly 1 item matching the source
        var singleItem = Assert.Single(target.Items);
        Assert.Equal("SourceChild1", singleItem.Name);
    }

    /// <summary>
    /// Regression test: When a complete update declares count=0 (empty collection),
    /// the target's existing items must all be removed.
    /// Bug scenario: Server's ObjectRef points to a new TestNode with empty Collection (count=0),
    /// but client's ObjectRef still has the old TestNode with 3 children. The Welcome sends
    /// count=0 for Collection, but without the fix the client never trims its items.
    /// </summary>
    [Fact]
    public void WhenCompleteUpdateHasEmptyCollection_ThenTargetCollectionIsCleared()
    {
        // Arrange - source has empty collection
        var sourceContext = InterceptorSubjectContext.Create().WithRegistry();
        var source = new CycleTestNode(sourceContext) { Name = "Root", Items = [] };

        var update = SubjectUpdate.CreateCompleteUpdate(source, []);

        // Arrange - target has 3 items (stale state)
        var targetContext = InterceptorSubjectContext.Create().WithRegistry();
        var target = new CycleTestNode(targetContext)
        {
            Name = "Root",
            Items =
            [
                new CycleTestNode { Name = "StaleChild1" },
                new CycleTestNode { Name = "StaleChild2" },
                new CycleTestNode { Name = "StaleChild3" }
            ]
        };

        // Act
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance);

        // Assert - target collection should be completely empty
        Assert.NotNull(target.Items);
        Assert.Empty(target.Items);
    }

    /// <summary>
    /// Regression test (inverse case): When a complete update declares more items than
    /// the target currently has, the target collection should be extended.
    /// This should already work without the trimming fix - this test guards against regressions.
    /// </summary>
    [Fact]
    public void WhenCompleteUpdateHasMoreItems_ThenTargetCollectionIsExtended()
    {
        // Arrange - source has 3 items
        var sourceContext = InterceptorSubjectContext.Create().WithRegistry();
        var source = new CycleTestNode(sourceContext)
        {
            Name = "Root",
            Items =
            [
                new CycleTestNode(sourceContext) { Name = "Child1" },
                new CycleTestNode(sourceContext) { Name = "Child2" },
                new CycleTestNode(sourceContext) { Name = "Child3" }
            ]
        };

        var update = SubjectUpdate.CreateCompleteUpdate(source, []);

        // Arrange - target has only 1 item
        var targetContext = InterceptorSubjectContext.Create().WithRegistry();
        var target = new CycleTestNode(targetContext)
        {
            Name = "Root",
            Items = [new CycleTestNode { Name = "ExistingChild" }]
        };

        // Act
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance);

        // Assert - target should have all 3 items from source
        Assert.Equal(3, target.Items.Count);
        Assert.Equal("Child1", target.Items[0].Name);
        Assert.Equal("Child2", target.Items[1].Name);
        Assert.Equal("Child3", target.Items[2].Name);
    }
}
