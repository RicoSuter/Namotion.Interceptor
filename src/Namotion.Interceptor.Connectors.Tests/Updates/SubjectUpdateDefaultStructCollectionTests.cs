using System.Collections.Immutable;
using System.Reactive.Concurrency;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Tests.Updates;

/// <summary>
/// Verifies that the connector update path treats the default instance of a struct collection as
/// an empty collection. Such a value holds a null inner array, so every read of it throws, and it
/// is what a collection property holds before anything is assigned to it.
/// </summary>
public class SubjectUpdateDefaultStructCollectionTests
{
    [Fact]
    public void WhenCollectionPropertyHoldsADefaultStruct_ThenCompleteUpdateReportsItEmpty()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var node = new StructCollectionNode(context) { Name = "Root" };

        // Act
        var update = SubjectUpdate.CreateCompleteUpdate(node, []);

        // Assert
        var itemsUpdate = update.Subjects[update.Root!][nameof(StructCollectionNode.ImmutableItems)];
        Assert.Equal(SubjectPropertyUpdateKind.Collection, itemsUpdate.Kind);
        Assert.Equal(0, itemsUpdate.Count);
        Assert.Empty(itemsUpdate.Items!);
    }

    [Fact]
    public void WhenCollectionPropertyStartsAsADefaultStruct_ThenAddingAnItemIsAnInsert()
    {
        // Arrange: the old value in the diff is the default struct the property was born with.
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions().WithRegistry();
        var node = new StructCollectionNode(context) { Name = "Root" };

        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(c => changes.Add(c));

        // Act
        node.ImmutableItems = [new StructCollectionNode { Name = "Item1" }];
        var update = SubjectUpdate.CreatePartialUpdateFromChanges(node, changes.ToArray(), []);

        // Assert
        var itemsUpdate = update.Subjects[update.Root!][nameof(StructCollectionNode.ImmutableItems)];
        Assert.NotNull(itemsUpdate.Operations);
        Assert.Single(itemsUpdate.Operations);
        Assert.Equal(SubjectCollectionOperationType.Insert, itemsUpdate.Operations[0].Action);
        Assert.Equal(0, itemsUpdate.Operations[0].Index);
    }

    [Fact]
    public void WhenCollectionPropertyIsSetToADefaultStruct_ThenItsItemsAreRemoved()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions().WithRegistry();
        var item1 = new StructCollectionNode { Name = "Item1" };
        var node = new StructCollectionNode(context)
        {
            Name = "Root",
            ImmutableItems = [item1]
        };

        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(c => changes.Add(c));

        // Act
        node.ImmutableItems = default;
        var update = SubjectUpdate.CreatePartialUpdateFromChanges(node, changes.ToArray(), []);

        // Assert
        var itemsUpdate = update.Subjects[update.Root!][nameof(StructCollectionNode.ImmutableItems)];
        Assert.Equal(0, itemsUpdate.Count);
        Assert.NotNull(itemsUpdate.Operations);
        Assert.Single(itemsUpdate.Operations);
        Assert.Equal(SubjectCollectionOperationType.Remove, itemsUpdate.Operations[0].Action);
    }

    [Fact]
    public void WhenTargetPropertyHoldsADefaultStruct_ThenApplyingACollectionUpdateSucceeds()
    {
        // Arrange: the apply step reads the target's current value to build its working list, so
        // the target, not the source, is what carries the unusable default here.
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var source = new StructCollectionNode(context)
        {
            Name = "Root",
            InterfaceItems = new List<StructCollectionNode> { new() { Name = "Item1" } }
        };

        var target = new StructCollectionNode(context)
        {
            Name = "Root",
            InterfaceItems = default(ImmutableArray<StructCollectionNode>)
        };

        var update = SubjectUpdate.CreateCompleteUpdate(source, []);

        // Act
        target.ApplySubjectUpdate(update, null, ChangeOrigin.Local);

        // Assert
        Assert.NotNull(target.InterfaceItems);
        Assert.Single(target.InterfaceItems);
        Assert.Equal("Item1", target.InterfaceItems[0].Name);
    }
}
