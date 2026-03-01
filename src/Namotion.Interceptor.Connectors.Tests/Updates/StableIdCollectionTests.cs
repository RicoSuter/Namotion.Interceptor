using System.Reactive.Concurrency;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Tests.Updates;

public class StableIdCollectionTests
{
    [Fact]
    public void CompleteUpdate_UsesStableBase62Ids()
    {
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var node = new CycleTestNode(context) { Name = "Root" };

        var update = SubjectUpdate.CreateCompleteUpdate(node, []);

        Assert.NotNull(update.Root);
        Assert.Equal(22, update.Root.Length);
        Assert.All(update.Root.ToCharArray(), c => Assert.True(char.IsLetterOrDigit(c)));
    }

    [Fact]
    public void CompleteUpdate_SameSubjectGetsSameIdAcrossUpdates()
    {
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var node = new CycleTestNode(context) { Name = "Root" };

        var update1 = SubjectUpdate.CreateCompleteUpdate(node, []);
        var update2 = SubjectUpdate.CreateCompleteUpdate(node, []);

        Assert.Equal(update1.Root, update2.Root);
    }

    [Fact]
    public void CollectionInsert_UsesAfterIdOfPredecessor()
    {
        var context = InterceptorSubjectContext.Create().WithPropertyChangeObservable().WithRegistry();
        var child1 = new CycleTestNode { Name = "Child1" };
        var node = new CycleTestNode(context) { Name = "Root", Items = [child1] };

        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(c => changes.Add(c));

        var child2 = new CycleTestNode { Name = "Child2" };
        node.Items = [child1, child2];

        var update = SubjectUpdate.CreatePartialUpdateFromChanges(
            node, changes.ToArray(), []);

        // Find the Items property update on the root subject
        var rootId = node.GetOrAddSubjectId();
        Assert.True(update.Subjects.ContainsKey(rootId));
        var itemsUpdate = update.Subjects[rootId]["Items"];
        Assert.NotNull(itemsUpdate.Operations);

        var insertOp = itemsUpdate.Operations.First(o => o.Action == SubjectCollectionOperationType.Insert);
        Assert.Equal(22, insertOp.Id.Length);
        // afterId should be child1's stable ID (insert after child1)
        Assert.Equal(child1.GetOrAddSubjectId(), insertOp.AfterId);
    }

    [Fact]
    public void CollectionRemove_UsesStableIdOfRemovedItem()
    {
        var context = InterceptorSubjectContext.Create().WithPropertyChangeObservable().WithRegistry();
        var child1 = new CycleTestNode { Name = "Child1" };
        var child2 = new CycleTestNode { Name = "Child2" };
        var node = new CycleTestNode(context) { Name = "Root", Items = [child1, child2] };

        var child2Id = child2.GetOrAddSubjectId();

        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(c => changes.Add(c));

        node.Items = [child1];

        var update = SubjectUpdate.CreatePartialUpdateFromChanges(
            node, changes.ToArray(), []);

        var rootId = node.GetOrAddSubjectId();
        var itemsUpdate = update.Subjects[rootId]["Items"];
        Assert.NotNull(itemsUpdate.Operations);

        var removeOp = itemsUpdate.Operations.First(o => o.Action == SubjectCollectionOperationType.Remove);
        Assert.Equal(child2Id, removeOp.Id);
    }

    [Fact]
    public void PartialValueUpdate_HasStableRootId()
    {
        var context = InterceptorSubjectContext.Create().WithPropertyChangeObservable().WithRegistry();
        var node = new CycleTestNode(context) { Name = "Root" };

        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(c => changes.Add(c));

        node.Name = "Updated";

        var update = SubjectUpdate.CreatePartialUpdateFromChanges(
            node, changes.ToArray(), []);

        // Root is always set to the stable ID of the root subject
        Assert.NotNull(update.Root);
        Assert.Equal(22, update.Root.Length);
        Assert.All(update.Root.ToCharArray(), c => Assert.True(char.IsLetterOrDigit(c)));
    }

    [Fact]
    public void CollectionInsertAtHead_HasNullAfterId()
    {
        var context = InterceptorSubjectContext.Create().WithPropertyChangeObservable().WithRegistry();
        var child2 = new CycleTestNode { Name = "Child2" };
        var node = new CycleTestNode(context) { Name = "Root", Items = [child2] };

        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(c => changes.Add(c));

        var child1 = new CycleTestNode { Name = "Child1" };
        node.Items = [child1, child2];

        var update = SubjectUpdate.CreatePartialUpdateFromChanges(
            node, changes.ToArray(), []);

        var rootId = node.GetOrAddSubjectId();
        var itemsUpdate = update.Subjects[rootId]["Items"];
        var insertOp = itemsUpdate.Operations!.First(o => o.Action == SubjectCollectionOperationType.Insert);

        Assert.Null(insertOp.AfterId); // inserted at head
    }
}
