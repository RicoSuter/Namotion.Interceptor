using System.Collections.Immutable;
using System.Reactive.Concurrency;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Tests.Updates;

/// <summary>
/// Verifies that the connector update path correctly classifies and round-trips properties whose
/// declared type is a read-only abstraction (<see cref="IReadOnlyList{T}"/>,
/// <see cref="IReadOnlyDictionary{TKey,TValue}"/>, <see cref="ImmutableArray{T}"/>). Runtime values
/// are concrete BCL types that still implement the non-generic dispatch interfaces.
/// </summary>
public class SubjectUpdateReadOnlyTypesTests
{
    [Fact]
    public void WhenImmutableArrayItemAdded_ThenInsertOperationIsCreated()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeObservable().WithRegistry();
        var item1 = new ReadOnlyTypesTestNode { Name = "Item1" };
        var node = new ReadOnlyTypesTestNode(context)
        {
            Name = "Root",
            ImmutableItems = [item1]
        };

        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(c => changes.Add(c));

        // Act
        var item2 = new ReadOnlyTypesTestNode { Name = "Item2" };
        node.ImmutableItems = [item1, item2];

        var update = SubjectUpdate.CreatePartialUpdateFromChanges(node, changes.ToArray(), []);

        // Assert
        var rootProps = update.Subjects[update.Root!];
        var itemsUpdate = rootProps[nameof(ReadOnlyTypesTestNode.ImmutableItems)];
        Assert.Equal(SubjectPropertyUpdateKind.Collection, itemsUpdate.Kind);
        Assert.NotNull(itemsUpdate.Operations);
        Assert.Single(itemsUpdate.Operations);
        Assert.Equal(SubjectCollectionOperationType.Insert, itemsUpdate.Operations[0].Action);
        Assert.Equal(1, itemsUpdate.Operations[0].Index);
    }

    [Fact]
    public void WhenIReadOnlyListItemRemoved_ThenRemoveOperationIsCreated()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeObservable().WithRegistry();
        var item1 = new ReadOnlyTypesTestNode { Name = "Item1" };
        var item2 = new ReadOnlyTypesTestNode { Name = "Item2" };
        var node = new ReadOnlyTypesTestNode(context)
        {
            Name = "Root",
            ReadOnlyItems = new List<ReadOnlyTypesTestNode> { item1, item2 }
        };

        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(c => changes.Add(c));

        // Act - remove last item
        node.ReadOnlyItems = new List<ReadOnlyTypesTestNode> { item1 };

        var update = SubjectUpdate.CreatePartialUpdateFromChanges(node, changes.ToArray(), []);

        // Assert
        var rootProps = update.Subjects[update.Root!];
        var itemsUpdate = rootProps[nameof(ReadOnlyTypesTestNode.ReadOnlyItems)];
        Assert.Equal(SubjectPropertyUpdateKind.Collection, itemsUpdate.Kind);
        Assert.NotNull(itemsUpdate.Operations);
        Assert.Single(itemsUpdate.Operations);
        Assert.Equal(SubjectCollectionOperationType.Remove, itemsUpdate.Operations[0].Action);
        Assert.Equal(1, itemsUpdate.Operations[0].Index);
    }

    [Fact]
    public void WhenIReadOnlyDictionaryKeyAdded_ThenInsertOperationIsCreated()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeObservable().WithRegistry();
        var item1 = new ReadOnlyTypesTestNode { Name = "Item1" };
        var node = new ReadOnlyTypesTestNode(context)
        {
            Name = "Root",
            ReadOnlyLookup = new Dictionary<string, ReadOnlyTypesTestNode> { ["key1"] = item1 }
        };

        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(c => changes.Add(c));

        // Act - add new key (runtime value is still a concrete Dictionary that implements IDictionary)
        var item2 = new ReadOnlyTypesTestNode { Name = "Item2" };
        node.ReadOnlyLookup = new Dictionary<string, ReadOnlyTypesTestNode>
        {
            ["key1"] = item1,
            ["key2"] = item2
        };

        var update = SubjectUpdate.CreatePartialUpdateFromChanges(node, changes.ToArray(), []);

        // Assert
        var rootProps = update.Subjects[update.Root!];
        var lookupUpdate = rootProps[nameof(ReadOnlyTypesTestNode.ReadOnlyLookup)];
        Assert.Equal(SubjectPropertyUpdateKind.Dictionary, lookupUpdate.Kind);
        Assert.NotNull(lookupUpdate.Operations);
        Assert.Single(lookupUpdate.Operations);
        Assert.Equal(SubjectCollectionOperationType.Insert, lookupUpdate.Operations[0].Action);
        Assert.Equal("key2", lookupUpdate.Operations[0].Index);
    }

    [Fact]
    public void WhenIReadOnlyDictionaryItemPropertyChanged_ThenSparseUpdateByKeyIsCreated()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeObservable().WithRegistry();
        var item1 = new ReadOnlyTypesTestNode { Name = "Item1" };
        var item2 = new ReadOnlyTypesTestNode { Name = "Item2" };
        var node = new ReadOnlyTypesTestNode(context)
        {
            Name = "Root",
            ReadOnlyLookup = new Dictionary<string, ReadOnlyTypesTestNode>
            {
                ["key1"] = item1,
                ["key2"] = item2
            }
        };

        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(c => changes.Add(c));

        // Act - sparse property change on a retained dictionary entry
        item1.Name = "Item1Updated";

        var update = SubjectUpdate.CreatePartialUpdateFromChanges(node, changes.ToArray(), []);

        // Assert
        var rootProps = update.Subjects[update.Root!];
        var lookupUpdate = rootProps[nameof(ReadOnlyTypesTestNode.ReadOnlyLookup)];
        Assert.Equal(SubjectPropertyUpdateKind.Dictionary, lookupUpdate.Kind);
        Assert.Null(lookupUpdate.Operations);
        Assert.NotNull(lookupUpdate.Items);
        Assert.Single(lookupUpdate.Items);
        Assert.Equal("key1", lookupUpdate.Items[0].Index);
    }

    [Fact]
    public void WhenImmutableArrayCompleteUpdate_ThenAllItemsIncluded()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var item1 = new ReadOnlyTypesTestNode { Name = "Item1" };
        var item2 = new ReadOnlyTypesTestNode { Name = "Item2" };
        var node = new ReadOnlyTypesTestNode(context)
        {
            Name = "Root",
            ImmutableItems = [item1, item2]
        };

        // Act
        var update = SubjectUpdate.CreateCompleteUpdate(node, []);

        // Assert
        var rootProps = update.Subjects[update.Root!];
        var itemsUpdate = rootProps[nameof(ReadOnlyTypesTestNode.ImmutableItems)];
        Assert.Equal(SubjectPropertyUpdateKind.Collection, itemsUpdate.Kind);
        Assert.Equal(2, itemsUpdate.Count);
        Assert.NotNull(itemsUpdate.Items);
        Assert.Equal(2, itemsUpdate.Items.Count);
        Assert.Equal(0, itemsUpdate.Items[0].Index);
        Assert.Equal(1, itemsUpdate.Items[1].Index);
    }

    [Fact]
    public void WhenSourceIsReadOnlyDictionaryWrapper_ThenAppliesToTargetCorrectly()
    {
        // Arrange: source's IReadOnlyDictionary property holds a wrapper that implements
        // ONLY IReadOnlyDictionary<,> (no non-generic IDictionary). This is the read-only
        // slow path - the value is materialized into a Dictionary via KVP reflection in
        // SubjectValueConvert.ToSubjectDictionary, and the apply step writes a fresh
        // Dictionary back onto the target.
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var sourceItem1 = new ReadOnlyTypesTestNode(context) { Name = "Item1" };
        var sourceItem2 = new ReadOnlyTypesTestNode(context) { Name = "Item2" };
        var source = new ReadOnlyTypesTestNode(context)
        {
            Name = "Root",
            ReadOnlyLookup = new ReadOnlyDictionaryWrapper<string, ReadOnlyTypesTestNode>(
                new Dictionary<string, ReadOnlyTypesTestNode>
                {
                    ["key1"] = sourceItem1,
                    ["key2"] = sourceItem2
                })
        };
        var target = new ReadOnlyTypesTestNode(context);

        // Act
        var update = SubjectUpdate.CreateCompleteUpdate(source, []);
        target.ApplySubjectUpdate(update, null);

        // Assert: target.ReadOnlyLookup contains the expected entries.
        Assert.Equal(2, target.ReadOnlyLookup.Count);
        Assert.True(target.ReadOnlyLookup.ContainsKey("key1"));
        Assert.True(target.ReadOnlyLookup.ContainsKey("key2"));
        Assert.Equal("Item1", target.ReadOnlyLookup["key1"].Name);
        Assert.Equal("Item2", target.ReadOnlyLookup["key2"].Name);
    }

    [Fact]
    public void WhenIReadOnlyDictionaryCompleteUpdate_ThenAllEntriesIncluded()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var item1 = new ReadOnlyTypesTestNode { Name = "Item1" };
        var item2 = new ReadOnlyTypesTestNode { Name = "Item2" };
        var node = new ReadOnlyTypesTestNode(context)
        {
            Name = "Root",
            ReadOnlyLookup = new Dictionary<string, ReadOnlyTypesTestNode>
            {
                ["key1"] = item1,
                ["key2"] = item2
            }
        };

        // Act
        var update = SubjectUpdate.CreateCompleteUpdate(node, []);

        // Assert
        var rootProps = update.Subjects[update.Root!];
        var lookupUpdate = rootProps[nameof(ReadOnlyTypesTestNode.ReadOnlyLookup)];
        Assert.Equal(SubjectPropertyUpdateKind.Dictionary, lookupUpdate.Kind);
        Assert.Equal(2, lookupUpdate.Count);
        Assert.NotNull(lookupUpdate.Items);
        Assert.Equal(2, lookupUpdate.Items.Count);
        var keys = lookupUpdate.Items.Select(i => i.Index).Cast<string>().OrderBy(k => k).ToArray();
        Assert.Equal(new[] { "key1", "key2" }, keys);
    }

}
