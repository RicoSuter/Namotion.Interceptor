using System.Collections.Immutable;
using System.Collections.ObjectModel;
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
    public void WhenImmutableArrayItemAdded_ThenCompleteItemStateIsCreated()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions().WithRegistry();
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
        Assert.NotNull(itemsUpdate.Items);
        Assert.Equal(2, itemsUpdate.Items.Count);
        Assert.Equal(item1.TryGetSubjectId(), itemsUpdate.Items[0].Id);
        Assert.Equal(item2.TryGetSubjectId(), itemsUpdate.Items[1].Id);
        Assert.Equal("Item2", update.Subjects[itemsUpdate.Items[1].Id][nameof(ReadOnlyTypesTestNode.Name)].Value);
    }

    [Fact]
    public void WhenIReadOnlyListItemRemoved_ThenCompleteItemStateIsCreated()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions().WithRegistry();
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
        Assert.NotNull(itemsUpdate.Items);
        Assert.Single(itemsUpdate.Items);
        Assert.Equal(item1.TryGetSubjectId(), itemsUpdate.Items[0].Id);
    }

    [Fact]
    public void WhenIReadOnlyDictionaryKeyAdded_ThenCompleteEntryStateIsCreated()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions().WithRegistry();
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
        Assert.NotNull(lookupUpdate.Items);
        Assert.Equal(2, lookupUpdate.Items.Count);
        var addedItem = Assert.Single(lookupUpdate.Items, item => item.Key == "key2");
        Assert.Equal(item2.TryGetSubjectId(), addedItem.Id);
        Assert.Equal("Item2", update.Subjects[addedItem.Id][nameof(ReadOnlyTypesTestNode.Name)].Value);
    }

    [Fact]
    public void WhenIReadOnlyDictionaryItemPropertyChanged_ThenItemSubjectIsUpdatedById()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions().WithRegistry();
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

        // Assert - the changed item is addressed by its own stable ID, the parent's dictionary
        // property is untouched because no structural change happened
        var itemId = item1.TryGetSubjectId();
        Assert.NotNull(itemId);
        var itemProperties = Assert.Contains(itemId!, update.Subjects);
        Assert.Equal("Item1Updated", itemProperties[nameof(ReadOnlyTypesTestNode.Name)].Value);
        Assert.DoesNotContain(
            update.Subjects,
            entry => entry.Value.ContainsKey(nameof(ReadOnlyTypesTestNode.ReadOnlyLookup)));
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
        Assert.NotNull(itemsUpdate.Items);
        Assert.Equal(2, itemsUpdate.Items.Count);
        Assert.Equal(item1.TryGetSubjectId(), itemsUpdate.Items[0].Id);
        Assert.Equal(item2.TryGetSubjectId(), itemsUpdate.Items[1].Id);
        Assert.All(itemsUpdate.Items, item => Assert.True(update.Subjects.ContainsKey(item.Id)));
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
        target.ApplySubjectUpdate(update, null, ChangeOrigin.Local);

        // Assert: target.ReadOnlyLookup contains the expected entries.
        Assert.Equal(2, target.ReadOnlyLookup.Count);
        Assert.True(target.ReadOnlyLookup.ContainsKey("key1"));
        Assert.True(target.ReadOnlyLookup.ContainsKey("key2"));
        Assert.Equal("Item1", target.ReadOnlyLookup["key1"].Name);
        Assert.Equal("Item2", target.ReadOnlyLookup["key2"].Name);
    }

    [Fact]
    public void WhenCompleteImmutableArrayUpdateIsApplied_ThenTargetHoldsAnImmutableArrayWithAllItems()
    {
        // Arrange - the target's declared type is not assignable from the applier's working List<T>,
        // so the collection has to be materialized into an ImmutableArray before it is written back.
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var sourceItem1 = new ReadOnlyTypesTestNode(context) { Name = "Item1" };
        var sourceItem2 = new ReadOnlyTypesTestNode(context) { Name = "Item2" };
        var source = new ReadOnlyTypesTestNode(context)
        {
            Name = "Root",
            ImmutableItems = [sourceItem1, sourceItem2]
        };
        var target = new ReadOnlyTypesTestNode(context);

        // Act
        var update = SubjectUpdate.CreateCompleteUpdate(source, []);
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Assert.IsType<ImmutableArray<ReadOnlyTypesTestNode>>(target.ImmutableItems);
        Assert.Equal(2, target.ImmutableItems.Length);
        Assert.Equal("Item1", target.ImmutableItems[0].Name);
        Assert.Equal("Item2", target.ImmutableItems[1].Name);
    }

    [Fact]
    public void WhenCompleteReadOnlyCollectionUpdateIsApplied_ThenTargetHoldsAReadOnlyCollectionWithAllItems()
    {
        // Arrange - ReadOnlyCollection<T> has no AddRange, so it is built through its IList<T>
        // constructor instead of the static-empty plus append path used for immutable collections.
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var sourceItem1 = new ReadOnlyTypesTestNode(context) { Name = "Item1" };
        var sourceItem2 = new ReadOnlyTypesTestNode(context) { Name = "Item2" };
        var source = new ReadOnlyTypesTestNode(context)
        {
            Name = "Root",
            WrappedItems = new ReadOnlyCollection<ReadOnlyTypesTestNode>(
                new List<ReadOnlyTypesTestNode> { sourceItem1, sourceItem2 })
        };
        var target = new ReadOnlyTypesTestNode(context);

        // Act
        var update = SubjectUpdate.CreateCompleteUpdate(source, []);
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Assert.IsType<ReadOnlyCollection<ReadOnlyTypesTestNode>>(target.WrappedItems);
        Assert.Equal(2, target.WrappedItems.Count);
        Assert.Equal("Item1", target.WrappedItems[0].Name);
        Assert.Equal("Item2", target.WrappedItems[1].Name);
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
        Assert.NotNull(lookupUpdate.Items);
        Assert.Equal(2, lookupUpdate.Items.Count);
        var keys = lookupUpdate.Items.Select(item => item.Key).OrderBy(key => key).ToArray();
        Assert.Equal(new[] { "key1", "key2" }, keys);
        Assert.All(lookupUpdate.Items, item => Assert.True(update.Subjects.ContainsKey(item.Id)));
    }
}
