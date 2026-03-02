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
    public void CollectionInsert_ProducesCompleteStateWithNewItemData()
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

        // Complete state: full items list
        Assert.NotNull(itemsUpdate.Items);
        Assert.Equal(2, itemsUpdate.Items.Count);

        // Both items referenced by stable IDs
        Assert.Equal(child1.GetOrAddSubjectId(), itemsUpdate.Items[0].Id);
        Assert.Equal(child2.GetOrAddSubjectId(), itemsUpdate.Items[1].Id);

        // New item (child2) should have full subject data in the update
        Assert.True(update.Subjects.ContainsKey(child2.GetOrAddSubjectId()));
    }

    [Fact]
    public void CollectionRemove_ProducesCompleteStateWithoutRemovedItem()
    {
        var context = InterceptorSubjectContext.Create().WithPropertyChangeObservable().WithRegistry();
        var child1 = new CycleTestNode { Name = "Child1" };
        var child2 = new CycleTestNode { Name = "Child2" };
        var node = new CycleTestNode(context) { Name = "Root", Items = [child1, child2] };

        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(c => changes.Add(c));

        node.Items = [child1];

        var update = SubjectUpdate.CreatePartialUpdateFromChanges(
            node, changes.ToArray(), []);

        var rootId = node.GetOrAddSubjectId();
        var itemsUpdate = update.Subjects[rootId]["Items"];

        // Complete state: items list with only remaining item
        Assert.NotNull(itemsUpdate.Items);
        Assert.Single(itemsUpdate.Items);
        Assert.Equal(child1.GetOrAddSubjectId(), itemsUpdate.Items[0].Id);
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
    public void CollectionInsertAtHead_ProducesCompleteStateWithCorrectOrdering()
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

        // Complete state with correct ordering (child1 first)
        Assert.NotNull(itemsUpdate.Items);
        Assert.Equal(2, itemsUpdate.Items.Count);
        Assert.Equal(child1.GetOrAddSubjectId(), itemsUpdate.Items[0].Id);
        Assert.Equal(child2.GetOrAddSubjectId(), itemsUpdate.Items[1].Id);
    }
}
