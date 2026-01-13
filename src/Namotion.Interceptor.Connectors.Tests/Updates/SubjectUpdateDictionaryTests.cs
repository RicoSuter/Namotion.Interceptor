using System.Reactive.Concurrency;
using System.Text.Json;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Tests.Updates;

/// <summary>
/// Tests for dictionary diff operations in SubjectUpdate.
/// Tests Insert (add key), Remove (delete key) operations and sparse property updates.
/// Note: Move is not applicable for dictionaries.
/// </summary>
public class SubjectUpdateDictionaryTests
{
    [Fact]
    public async Task WhenKeyAdded_ThenInsertOperationIsCreated()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeObservable().WithRegistry();
        var item1 = new CycleTestNode { Name = "Item1" };
        var node = new CycleTestNode(context)
        {
            Name = "Root",
            Lookup = new Dictionary<string, CycleTestNode> { ["key1"] = item1 }
        };

        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(c => changes.Add(c));

        // Act - add new key
        var item2 = new CycleTestNode { Name = "Item2" };
        node.Lookup = new Dictionary<string, CycleTestNode> { ["key1"] = item1, ["key2"] = item2 };

        var update = SubjectUpdate.CreatePartialUpdateFromChanges(node, changes.ToArray(), []);

        // Assert
        await Verify(update);
    }

    [Fact]
    public async Task WhenKeyRemoved_ThenRemoveOperationIsCreated()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeObservable().WithRegistry();
        var item1 = new CycleTestNode { Name = "Item1" };
        var item2 = new CycleTestNode { Name = "Item2" };
        var node = new CycleTestNode(context)
        {
            Name = "Root",
            Lookup = new Dictionary<string, CycleTestNode> { ["key1"] = item1, ["key2"] = item2 }
        };

        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(c => changes.Add(c));

        // Act - remove key
        node.Lookup = new Dictionary<string, CycleTestNode> { ["key2"] = item2 };

        var update = SubjectUpdate.CreatePartialUpdateFromChanges(node, changes.ToArray(), []);

        // Assert
        await Verify(update);
    }

    [Fact]
    public async Task WhenItemPropertyChanged_ThenSparseUpdateByKeyIsCreated()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeObservable().WithRegistry();
        var item1 = new CycleTestNode { Name = "Item1" };
        var item2 = new CycleTestNode { Name = "Item2" };
        var node = new CycleTestNode(context)
        {
            Name = "Root",
            Lookup = new Dictionary<string, CycleTestNode> { ["key1"] = item1, ["key2"] = item2 }
        };

        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(c => changes.Add(c));

        // Act - update property on item (no structural change)
        item1.Name = "Item1Updated";

        var update = SubjectUpdate.CreatePartialUpdateFromChanges(node, changes.ToArray(), []);

        // Assert - should have Collection update at key1, not Operations
        await Verify(update);
    }

    [Fact]
    public async Task WhenMultipleItemsHavePropertyChanges_ThenSparseUpdatesAreCreated()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeObservable().WithRegistry();
        var item1 = new CycleTestNode { Name = "Item1" };
        var item2 = new CycleTestNode { Name = "Item2" };
        var item3 = new CycleTestNode { Name = "Item3" };
        var node = new CycleTestNode(context)
        {
            Name = "Root",
            Lookup = new Dictionary<string, CycleTestNode>
            {
                ["key1"] = item1,
                ["key2"] = item2,
                ["key3"] = item3
            }
        };

        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(c => changes.Add(c));

        // Act - update properties on two items
        item1.Name = "Item1Updated";
        item3.Name = "Item3Updated";

        var update = SubjectUpdate.CreatePartialUpdateFromChanges(node, changes.ToArray(), []);

        // Assert - should have sparse Collection updates at key1 and key3
        await Verify(update);
    }

    [Fact]
    public async Task WhenAddAndRemoveCombined_ThenBothOperationsAreCreated()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeObservable().WithRegistry();
        var item1 = new CycleTestNode { Name = "Item1" };
        var item2 = new CycleTestNode { Name = "Item2" };
        var node = new CycleTestNode(context)
        {
            Name = "Root",
            Lookup = new Dictionary<string, CycleTestNode> { ["key1"] = item1, ["key2"] = item2 }
        };

        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(c => changes.Add(c));

        // Act - remove key1, add key3
        var item3 = new CycleTestNode { Name = "Item3" };
        node.Lookup = new Dictionary<string, CycleTestNode> { ["key2"] = item2, ["key3"] = item3 };

        var update = SubjectUpdate.CreatePartialUpdateFromChanges(node, changes.ToArray(), []);

        // Assert
        await Verify(update);
    }

    [Fact]
    public async Task WhenInsertWithPropertyUpdateOnExisting_ThenBothTypesAreCreated()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeObservable().WithRegistry();
        var item1 = new CycleTestNode { Name = "Item1" };
        var node = new CycleTestNode(context)
        {
            Name = "Root",
            Lookup = new Dictionary<string, CycleTestNode> { ["key1"] = item1 }
        };

        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(c => changes.Add(c));

        // Act - update existing AND add new key
        item1.Name = "Item1Updated";
        var item2 = new CycleTestNode { Name = "Item2" };
        node.Lookup = new Dictionary<string, CycleTestNode> { ["key1"] = item1, ["key2"] = item2 };

        var update = SubjectUpdate.CreatePartialUpdateFromChanges(node, changes.ToArray(), []);

        // Assert
        await Verify(update);
    }

    [Fact]
    public async Task WhenDictionaryBecomesEmpty_ThenRemoveOperationsAreCreated()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeObservable().WithRegistry();
        var item1 = new CycleTestNode { Name = "Item1" };
        var item2 = new CycleTestNode { Name = "Item2" };
        var node = new CycleTestNode(context)
        {
            Name = "Root",
            Lookup = new Dictionary<string, CycleTestNode> { ["key1"] = item1, ["key2"] = item2 }
        };

        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(c => changes.Add(c));

        // Act - clear dictionary
        node.Lookup = new Dictionary<string, CycleTestNode>();

        var update = SubjectUpdate.CreatePartialUpdateFromChanges(node, changes.ToArray(), []);

        // Assert
        await Verify(update);
    }

    [Fact]
    public async Task WhenDictionaryPopulatedFromEmpty_ThenInsertOperationsAreCreated()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeObservable().WithRegistry();
        var node = new CycleTestNode(context)
        {
            Name = "Root",
            Lookup = new Dictionary<string, CycleTestNode>()
        };

        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(c => changes.Add(c));

        // Act - populate from empty
        var item1 = new CycleTestNode { Name = "Item1" };
        var item2 = new CycleTestNode { Name = "Item2" };
        node.Lookup = new Dictionary<string, CycleTestNode> { ["key1"] = item1, ["key2"] = item2 };

        var update = SubjectUpdate.CreatePartialUpdateFromChanges(node, changes.ToArray(), []);

        // Assert
        await Verify(update);
    }

    [Fact]
    public async Task WhenCompleteDictionaryUpdate_ThenAllItemsIncluded()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var item1 = new CycleTestNode { Name = "Item1" };
        var item2 = new CycleTestNode { Name = "Item2" };
        var node = new CycleTestNode(context)
        {
            Name = "Root",
            Lookup = new Dictionary<string, CycleTestNode> { ["key1"] = item1, ["key2"] = item2 }
        };

        // Act - create complete update
        var update = SubjectUpdate.CreateCompleteUpdate(node, []);

        // Assert - Collection should have all items with full data
        await Verify(update);
    }

    [Fact]
    public void WhenDictionaryHasIntKeys_ThenKeysArePreservedAfterJsonRoundTrip()
    {
        // Arrange - use dedicated model to avoid affecting other snapshot tests
        var sourceContext = InterceptorSubjectContext.Create().WithRegistry();
        var item1 = new IntKeyDictionaryNode { Name = "Item1" };
        var item2 = new IntKeyDictionaryNode { Name = "Item2" };
        var source = new IntKeyDictionaryNode(sourceContext)
        {
            Name = "Root",
            Children = new Dictionary<int, IntKeyDictionaryNode> { [42] = item1, [123] = item2 }
        };

        // Act - create update, serialize to JSON, deserialize, and apply
        var update = SubjectUpdate.CreateCompleteUpdate(source, []);
        var json = JsonSerializer.Serialize(update);
        var deserializedUpdate = JsonSerializer.Deserialize<SubjectUpdate>(json)!;

        var targetContext = InterceptorSubjectContext.Create().WithRegistry();
        var target = new IntKeyDictionaryNode(targetContext) { Name = "Target" };
        target.ApplySubjectUpdate(deserializedUpdate, DefaultSubjectFactory.Instance);

        // Assert - int keys should be preserved after JSON round-trip
        Assert.Equal(2, target.Children!.Count);
        Assert.True(target.Children.ContainsKey(42), "Key 42 should exist");
        Assert.True(target.Children.ContainsKey(123), "Key 123 should exist");
        Assert.Equal("Item1", target.Children[42].Name);
        Assert.Equal("Item2", target.Children[123].Name);
    }
}
