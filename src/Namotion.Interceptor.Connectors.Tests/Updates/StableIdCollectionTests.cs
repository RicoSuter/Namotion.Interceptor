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
    public void WhenCompleteUpdateIsCreated_ThenRootUsesStableBase62Id()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var node = new CycleTestNode(context) { Name = "Root" };

        // Act
        var update = SubjectUpdate.CreateCompleteUpdate(node, []);

        // Assert
        Assert.NotNull(update.Root);
        Assert.Equal(22, update.Root.Length);
        Assert.All(update.Root.ToCharArray(), c => Assert.True(char.IsLetterOrDigit(c)));
    }

    [Fact]
    public void WhenCompleteUpdateIsCreatedTwice_ThenSameSubjectKeepsItsId()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var node = new CycleTestNode(context) { Name = "Root" };

        // Act
        var update1 = SubjectUpdate.CreateCompleteUpdate(node, []);
        var update2 = SubjectUpdate.CreateCompleteUpdate(node, []);

        // Assert
        Assert.Equal(update1.Root, update2.Root);
    }

    [Fact]
    public void WhenCollectionItemIsInserted_ThenCompleteStateCarriesNewItemData()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions().WithRegistry();
        var child1 = new CycleTestNode { Name = "Child1" };
        var node = new CycleTestNode(context) { Name = "Root", Items = [child1] };

        var changes = new List<SubjectPropertyChange>();
        using var subscription = context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(c => changes.Add(c));

        // Act
        var child2 = new CycleTestNode { Name = "Child2" };
        node.Items = [child1, child2];

        var update = SubjectUpdate.CreatePartialUpdateFromChanges(
            node, changes.ToArray(), []);

        // Assert - the Items property update on the root subject carries complete state
        var rootId = node.GetOrAddSubjectId();
        Assert.True(update.Subjects.ContainsKey(rootId));
        var itemsUpdate = update.Subjects[rootId]["Items"];

        Assert.NotNull(itemsUpdate.Items);
        Assert.Equal(2, itemsUpdate.Items.Count);

        // Both items referenced by stable IDs
        Assert.Equal(child1.GetOrAddSubjectId(), itemsUpdate.Items[0].Id);
        Assert.Equal(child2.GetOrAddSubjectId(), itemsUpdate.Items[1].Id);

        // New item (child2) should have full subject data in the update
        Assert.True(update.Subjects.ContainsKey(child2.GetOrAddSubjectId()));
    }

    [Fact]
    public void WhenCollectionItemIsRemoved_ThenCompleteStateOmitsRemovedItem()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions().WithRegistry();
        var child1 = new CycleTestNode { Name = "Child1" };
        var child2 = new CycleTestNode { Name = "Child2" };
        var node = new CycleTestNode(context) { Name = "Root", Items = [child1, child2] };

        var changes = new List<SubjectPropertyChange>();
        using var subscription = context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(c => changes.Add(c));

        // Act
        node.Items = [child1];

        var update = SubjectUpdate.CreatePartialUpdateFromChanges(
            node, changes.ToArray(), []);

        // Assert - complete state with only the remaining item
        var rootId = node.GetOrAddSubjectId();
        var itemsUpdate = update.Subjects[rootId]["Items"];

        Assert.NotNull(itemsUpdate.Items);
        Assert.Single(itemsUpdate.Items);
        Assert.Equal(child1.GetOrAddSubjectId(), itemsUpdate.Items[0].Id);
    }

    [Fact]
    public void WhenPartialValueUpdateIsCreated_ThenRootIsTheStableSubjectId()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions().WithRegistry();
        var node = new CycleTestNode(context) { Name = "Root" };

        var changes = new List<SubjectPropertyChange>();
        using var subscription = context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(c => changes.Add(c));

        // Act
        node.Name = "Updated";

        var update = SubjectUpdate.CreatePartialUpdateFromChanges(
            node, changes.ToArray(), []);

        // Assert - Root is always set to the stable ID of the root subject
        Assert.NotNull(update.Root);
        Assert.Equal(22, update.Root.Length);
        Assert.All(update.Root.ToCharArray(), c => Assert.True(char.IsLetterOrDigit(c)));
    }

    [Fact]
    public void WhenCollectionItemIsInsertedAtHead_ThenCompleteStatePreservesOrdering()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions().WithRegistry();
        var child2 = new CycleTestNode { Name = "Child2" };
        var node = new CycleTestNode(context) { Name = "Root", Items = [child2] };

        var changes = new List<SubjectPropertyChange>();
        using var subscription = context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Subscribe(c => changes.Add(c));

        // Act
        var child1 = new CycleTestNode { Name = "Child1" };
        node.Items = [child1, child2];

        var update = SubjectUpdate.CreatePartialUpdateFromChanges(
            node, changes.ToArray(), []);

        // Assert - complete state with correct ordering (child1 first)
        var rootId = node.GetOrAddSubjectId();
        var itemsUpdate = update.Subjects[rootId]["Items"];

        Assert.NotNull(itemsUpdate.Items);
        Assert.Equal(2, itemsUpdate.Items.Count);
        Assert.Equal(child1.GetOrAddSubjectId(), itemsUpdate.Items[0].Id);
        Assert.Equal(child2.GetOrAddSubjectId(), itemsUpdate.Items[1].Id);
    }
}
