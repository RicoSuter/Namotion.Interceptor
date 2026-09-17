using System.Reactive.Concurrency;
using System.Text.Json;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Tests.Updates;

/// <summary>
/// Verifies how the applier treats a property this model cannot write. A producer may publish one for
/// a receiver to display, so it is an expected shape rather than a failure, but a reference or
/// container the receiver already holds still has to carry the update through to its subtree.
/// </summary>
public class SubjectUpdateUnwritablePropertyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhenAReferenceIsInitOnly_ThenTheNestedChangeStillReachesTheExistingChild(bool partial)
    {
        // Arrange
        var sourceContext = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions().WithRegistry();
        var sourceChild = new InitOnlyTypesTestNode { Name = "Stale" };
        var source = new InitOnlyTypesTestNode(sourceContext) { Name = "Root", Child = sourceChild };
        var target = new InitOnlyTypesTestNode(InterceptorSubjectContext.Create().WithRegistry())
        {
            Name = "Root",
            Child = new InitOnlyTypesTestNode { Name = "Stale" }
        };
        var existingChild = target.Child!;

        var changes = new List<SubjectPropertyChange>();
        sourceContext.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(changes.Add);
        sourceChild.Name = "Alice";

        // Act
        var update = partial
            ? SubjectUpdate.CreatePartialUpdateFromChanges(source, changes.ToArray(), [])
            : SubjectUpdate.CreateCompleteUpdate(source, []);
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Assert.Same(existingChild, target.Child);
        Assert.Equal("Alice", target.Child!.Name);
    }

    [Fact]
    public void WhenACollectionIsInitOnly_ThenASparseItemUpdateStillReachesTheExistingItem()
    {
        // Arrange
        var sourceContext = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions().WithRegistry();
        var sourceItem = new InitOnlyTypesTestNode { Name = "Stale" };
        var source = new InitOnlyTypesTestNode(sourceContext) { Name = "Root", Items = [sourceItem] };
        var target = new InitOnlyTypesTestNode(InterceptorSubjectContext.Create().WithRegistry())
        {
            Name = "Root",
            Items = [new InitOnlyTypesTestNode { Name = "Stale" }]
        };
        var existingItem = target.Items[0];

        var changes = new List<SubjectPropertyChange>();
        sourceContext.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(changes.Add);
        sourceItem.Name = "Alice";

        // Act
        var update = SubjectUpdate.CreatePartialUpdateFromChanges(source, changes.ToArray(), []);
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Assert.Same(existingItem, Assert.Single(target.Items));
        Assert.Equal("Alice", target.Items[0].Name);
    }

    [Fact]
    public void WhenACollectionIsInitOnly_ThenCompleteMembershipDoesNotRebuildIt()
    {
        // Arrange: membership the receiver cannot store, so no item is created for it and the container
        // keeps the one it already holds.
        var target = new InitOnlyTypesTestNode(InterceptorSubjectContext.Create().WithRegistry())
        {
            Items = [new InitOnlyTypesTestNode { Name = "Existing" }]
        };
        var existingItem = target.Items[0];
        var update = new SubjectUpdate
        {
            Root = "1",
            Subjects = new Dictionary<string, Dictionary<string, SubjectPropertyUpdate>>
            {
                ["1"] = new()
                {
                    [nameof(InitOnlyTypesTestNode.Items)] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Collection,
                        Count = 2,
                        Items =
                        [
                            new SubjectPropertyItemUpdate { Index = 0, Id = "2" },
                            new SubjectPropertyItemUpdate { Index = 1, Id = "3" }
                        ]
                    }
                },
                ["2"] = new()
                {
                    [nameof(InitOnlyTypesTestNode.Name)] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Value,
                        Value = "Updated"
                    }
                },
                ["3"] = new()
                {
                    [nameof(InitOnlyTypesTestNode.Name)] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Value,
                        Value = "Added"
                    }
                }
            }
        };

        // Act
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Assert.Same(existingItem, Assert.Single(target.Items));
        Assert.Equal("Updated", target.Items[0].Name);
    }

    [Fact]
    public void WhenAValueUpdateNamesAPropertyWithoutASetter_ThenItIsIgnoredWithoutFailure()
    {
        // Arrange: the payload does not even convert to the property type, so a value this model
        // cannot store must be dropped before the conversion rather than reported as a failure.
        var target = new InitOnlyTypesTestNode(InterceptorSubjectContext.Create().WithRegistry())
        {
            Label = "Original"
        };
        var update = new SubjectUpdate
        {
            Root = "1",
            Subjects = new Dictionary<string, Dictionary<string, SubjectPropertyUpdate>>
            {
                ["1"] = new()
                {
                    [nameof(InitOnlyTypesTestNode.Label)] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Value,
                        Value = JsonSerializer.SerializeToElement(42)
                    },
                    [nameof(InitOnlyTypesTestNode.Name)] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Value,
                        Value = "Applied"
                    }
                }
            }
        };

        // Act
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Assert.Equal("Original", target.Label);
        Assert.Equal("Applied", target.Name);
    }
}
