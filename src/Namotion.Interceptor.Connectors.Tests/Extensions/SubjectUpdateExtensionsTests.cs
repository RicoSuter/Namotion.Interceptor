using System.Reactive.Linq;
using System.Text.Json;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Tests.Extensions;

public class SubjectUpdateExtensionsTests
{
    [Fact]
    public async Task WhenApplyingSimpleProperty_ThenItWorks()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var source = new Person(context) { FirstName = "John", LastName = "Doe" };
        var target = new Person(context);

        // Act
        var update = SubjectUpdate.CreateCompleteUpdate(source, []);
        await Verify(update);
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance);

        // Assert
        Assert.Equal("John", target.FirstName);
        Assert.Equal("Doe", target.LastName);
    }

    [Fact]
    public async Task WhenApplyingSimplePropertyWithTimestamp_ThenTimestampIsPreserved()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var timestamp = new DateTimeOffset(2024, 1, 15, 10, 30, 0, TimeSpan.Zero);

        Person source;
        using (SubjectChangeContext.WithChangedTimestamp(timestamp))
        {
            source = new Person(context) { FirstName = "John", LastName = "Doe" };
        }

        var target = new Person(context);

        // Act
        var update = SubjectUpdate.CreateCompleteUpdate(source, []);
        await Verify(update);
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance);

        // Assert
        Assert.Equal(timestamp, target.GetPropertyReference("FirstName").TryGetWriteTimestamp());
    }

    [Fact]
    public async Task WhenApplyingNestedProperty_ThenItWorks()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var source = new Person(context)
        {
            FirstName = "Child",
            Father = new Person(context) { FirstName = "Father" }
        };
        var target = new Person(context);

        // Act
        var update = SubjectUpdate.CreateCompleteUpdate(source, []);
        await Verify(update);
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance);

        // Assert
        Assert.Equal("Child", target.FirstName);
        Assert.NotNull(target.Father);
        Assert.Equal("Father", target.Father.FirstName);
    }

    [Fact]
    public async Task WhenApplyingCollectionProperty_ThenItWorks()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var source = new Person(context)
        {
            FirstName = "Parent",
            Children =
            [
                new Person(context) { FirstName = "Child1" },
                new Person(context) { FirstName = "Child2" }
            ]
        };
        var target = new Person(context);

        // Act
        var update = SubjectUpdate.CreateCompleteUpdate(source, []);
        await Verify(update);
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance);

        // Assert
        Assert.Equal(2, target.Children.Count);
        Assert.Equal("Child1", target.Children[0].FirstName);
        Assert.Equal("Child2", target.Children[1].FirstName);
    }

    [Fact]
    public async Task WhenApplyingPartialUpdate_ThenOnlyChangedPropertiesAreUpdated()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var source = new Person(context) { FirstName = "Initial", LastName = "Name" };
        var target = new Person(context) { FirstName = "Initial", LastName = "Name" };

        // Make a change to source
        var changes = new List<SubjectPropertyChange>();
        using (context.GetPropertyChangeObservable(System.Reactive.Concurrency.ImmediateScheduler.Instance)
            .Subscribe(c => changes.Add(c)))
        {
            source.FirstName = "Updated";
        }

        // Act
        var update = SubjectUpdate.CreatePartialUpdateFromChanges(source, changes.ToArray(), []);
        await Verify(update);
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance);

        // Assert
        Assert.Equal("Updated", target.FirstName);
        Assert.Equal("Name", target.LastName); // Unchanged
    }

    [Fact]
    public async Task WhenApplyingCollectionInsert_ThenItemIsAdded()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var source = new Person(context)
        {
            FirstName = "Parent",
            Children = [new Person(context) { FirstName = "ExistingChild" }]
        };
        var target = new Person(context)
        {
            FirstName = "Parent",
            Children = [new Person(context) { FirstName = "ExistingChild" }]
        };

        // Make a change - add a child
        var changes = new List<SubjectPropertyChange>();
        using (context.GetPropertyChangeObservable(System.Reactive.Concurrency.ImmediateScheduler.Instance)
            .Subscribe(c => changes.Add(c)))
        {
            source.Children = [..source.Children, new Person(context) { FirstName = "NewChild" }];
        }

        // Act
        var update = SubjectUpdate.CreatePartialUpdateFromChanges(source, changes.ToArray(), []);
        await Verify(update);
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance);

        // Assert
        Assert.Equal(2, target.Children.Count);
        Assert.Equal("NewChild", target.Children[1].FirstName);
    }

    [Fact]
    public async Task WhenApplyingCollectionRemove_ThenItemIsRemoved()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var child1 = new Person(context) { FirstName = "Child1" };
        var child2 = new Person(context) { FirstName = "Child2" };
        var source = new Person(context) { FirstName = "Parent", Children = [child1, child2] };

        var targetChild1 = new Person(context) { FirstName = "Child1" };
        var targetChild2 = new Person(context) { FirstName = "Child2" };
        var target = new Person(context) { FirstName = "Parent", Children = [targetChild1, targetChild2] };

        // Make a change - remove first child
        var changes = new List<SubjectPropertyChange>();
        using (context.GetPropertyChangeObservable(System.Reactive.Concurrency.ImmediateScheduler.Instance)
            .Subscribe(c => changes.Add(c)))
        {
            source.Children = [child2];
        }

        // Act
        var update = SubjectUpdate.CreatePartialUpdateFromChanges(source, changes.ToArray(), []);
        await Verify(update);
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance);

        // Assert
        Assert.Single(target.Children);
        Assert.Equal("Child2", target.Children[0].FirstName);
    }

    [Fact]
    public async Task WhenApplyingCollectionMove_ThenItemIsMoved()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var child1 = new Person(context) { FirstName = "Child1" };
        var child2 = new Person(context) { FirstName = "Child2" };
        var child3 = new Person(context) { FirstName = "Child3" };
        var source = new Person(context) { FirstName = "Parent", Children = [child1, child2, child3] };

        var targetChild1 = new Person(context) { FirstName = "Child1" };
        var targetChild2 = new Person(context) { FirstName = "Child2" };
        var targetChild3 = new Person(context) { FirstName = "Child3" };
        var target = new Person(context) { FirstName = "Parent", Children = [targetChild1, targetChild2, targetChild3] };

        // Make a change - move child3 to front
        var changes = new List<SubjectPropertyChange>();
        using (context.GetPropertyChangeObservable(System.Reactive.Concurrency.ImmediateScheduler.Instance)
            .Subscribe(c => changes.Add(c)))
        {
            source.Children = [child3, child1, child2];
        }

        // Act
        var update = SubjectUpdate.CreatePartialUpdateFromChanges(source, changes.ToArray(), []);
        await Verify(update);
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance);

        // Assert
        Assert.Equal(3, target.Children.Count);
        Assert.Equal("Child3", target.Children[0].FirstName);
        Assert.Equal("Child1", target.Children[1].FirstName);
        Assert.Equal("Child2", target.Children[2].FirstName);
    }

    [Fact]
    public async Task WhenApplyingNestedPropertyUpdate_ThenNestedItemIsUpdated()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var child = new Person(context) { FirstName = "OriginalChild" };
        var source = new Person(context) { FirstName = "Parent", Children = [child] };

        var targetChild = new Person(context) { FirstName = "OriginalChild" };
        var target = new Person(context) { FirstName = "Parent", Children = [targetChild] };

        // Make a change - update nested child's property
        var changes = new List<SubjectPropertyChange>();
        using (context.GetPropertyChangeObservable(System.Reactive.Concurrency.ImmediateScheduler.Instance)
            .Subscribe(c => changes.Add(c)))
        {
            child.FirstName = "UpdatedChild";
        }

        // Act
        var update = SubjectUpdate.CreatePartialUpdateFromChanges(source, changes.ToArray(), []);
        await Verify(update);
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance);

        // Assert
        Assert.Same(targetChild, target.Children[0]); // Same instance reused
        Assert.Equal("UpdatedChild", target.Children[0].FirstName);
    }

    [Fact]
    public async Task WhenApplyingDictionaryComplete_ThenDictionaryIsPopulated()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var source = new CycleTestNode(context)
        {
            Name = "Root",
            Lookup = new Dictionary<string, CycleTestNode>
            {
                ["key1"] = new CycleTestNode(context) { Name = "Item1" },
                ["key2"] = new CycleTestNode(context) { Name = "Item2" }
            }
        };
        var target = new CycleTestNode(context);

        // Act
        var update = SubjectUpdate.CreateCompleteUpdate(source, []);
        await Verify(update);
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance);

        // Assert
        Assert.Equal(2, target.Lookup.Count);
        Assert.Equal("Item1", target.Lookup["key1"].Name);
        Assert.Equal("Item2", target.Lookup["key2"].Name);
    }

    [Fact]
    public async Task WhenApplyingDictionaryInsert_ThenItemIsAdded()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var existingItem = new CycleTestNode(context) { Name = "Existing" };
        var source = new CycleTestNode(context)
        {
            Name = "Root",
            Lookup = new Dictionary<string, CycleTestNode> { ["existing"] = existingItem }
        };
        var target = new CycleTestNode(context)
        {
            Name = "Root",
            Lookup = new Dictionary<string, CycleTestNode>
            {
                ["existing"] = new CycleTestNode(context) { Name = "Existing" }
            }
        };

        // Make a change - add new key
        var changes = new List<SubjectPropertyChange>();
        using (context.GetPropertyChangeObservable(System.Reactive.Concurrency.ImmediateScheduler.Instance)
            .Subscribe(c => changes.Add(c)))
        {
            source.Lookup = new Dictionary<string, CycleTestNode>
            {
                ["existing"] = existingItem,
                ["newKey"] = new CycleTestNode(context) { Name = "NewItem" }
            };
        }

        // Act
        var update = SubjectUpdate.CreatePartialUpdateFromChanges(source, changes.ToArray(), []);
        await Verify(update);
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance);

        // Assert
        Assert.Equal(2, target.Lookup.Count);
        Assert.Equal("NewItem", target.Lookup["newKey"].Name);
    }

    [Fact]
    public async Task WhenApplyingDictionaryRemove_ThenItemIsRemoved()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var item1 = new CycleTestNode(context) { Name = "Item1" };
        var item2 = new CycleTestNode(context) { Name = "Item2" };
        var source = new CycleTestNode(context)
        {
            Name = "Root",
            Lookup = new Dictionary<string, CycleTestNode> { ["key1"] = item1, ["key2"] = item2 }
        };
        var target = new CycleTestNode(context)
        {
            Name = "Root",
            Lookup = new Dictionary<string, CycleTestNode>
            {
                ["key1"] = new CycleTestNode(context) { Name = "Item1" },
                ["key2"] = new CycleTestNode(context) { Name = "Item2" }
            }
        };

        // Make a change - remove key1
        var changes = new List<SubjectPropertyChange>();
        using (context.GetPropertyChangeObservable(System.Reactive.Concurrency.ImmediateScheduler.Instance)
            .Subscribe(c => changes.Add(c)))
        {
            source.Lookup = new Dictionary<string, CycleTestNode> { ["key2"] = item2 };
        }

        // Act
        var update = SubjectUpdate.CreatePartialUpdateFromChanges(source, changes.ToArray(), []);
        await Verify(update);
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance);

        // Assert
        Assert.Single(target.Lookup);
        Assert.False(target.Lookup.ContainsKey("key1"));
        Assert.Equal("Item2", target.Lookup["key2"].Name);
    }

    [Fact]
    public async Task WhenApplyingCircularReference_ThenItWorks()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var parent = new CycleTestNode(context) { Name = "Parent" };
        var child = new CycleTestNode(context) { Name = "Child", Parent = parent };
        parent.Child = child;

        var target = new CycleTestNode(context);

        // Act
        var update = SubjectUpdate.CreateCompleteUpdate(parent, []);
        await Verify(update);
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance);

        // Assert
        Assert.Equal("Parent", target.Name);
        Assert.NotNull(target.Child);
        Assert.Equal("Child", target.Child.Name);
        // Note: The circular reference back to parent won't be restored since
        // we create new instances. This is expected behavior.
    }

    [Fact]
    public async Task WhenApplyingSelfReference_ThenItWorks()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var node = new CycleTestNode(context) { Name = "SelfRef" };
        node.Self = node;

        var target = new CycleTestNode(context);

        // Act
        var update = SubjectUpdate.CreateCompleteUpdate(node, []);
        await Verify(update);
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance);

        // Assert
        Assert.Equal("SelfRef", target.Name);
        // Self-reference won't be restored to point to target itself
    }

    [Fact]
    public async Task WhenApplyingWithJsonElementValues_ThenValuesAreConverted()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var target = new Person(context);

        // Create update with JsonElement values (simulating JSON deserialization)
        var json = """{"value": "JsonValue"}""";
        var jsonElement = JsonDocument.Parse(json).RootElement.GetProperty("value");

        var update = new SubjectUpdate
        {
            Root = "1",
            Subjects = new Dictionary<string, Dictionary<string, SubjectPropertyUpdate>>
            {
                ["1"] = new Dictionary<string, SubjectPropertyUpdate>
                {
                    ["FirstName"] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Value,
                        Value = jsonElement
                    }
                }
            }
        };

        // Act
        await Verify(update);
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance);

        // Assert
        Assert.Equal("JsonValue", target.FirstName);
    }

    [Fact]
    public async Task WhenApplyingFromSource_ThenSourceIsTracked()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var source = new Person(context) { FirstName = "John" };
        var target = new Person(context);
        var externalSource = new object();

        // Capture FirstName change specifically (FullName is derived and fires without source)
        SubjectPropertyChange? capturedChange = null;
        using var subscription = context
            .GetPropertyChangeObservable(System.Reactive.Concurrency.ImmediateScheduler.Instance)
            .Where(c => c.Property.Name == "FirstName")
            .Subscribe(c => capturedChange = c);

        // Act
        var update = SubjectUpdate.CreateCompleteUpdate(source, []);
        await Verify(update);
        target.ApplySubjectUpdateFromSource(update, externalSource, DefaultSubjectFactory.Instance);

        // Assert
        Assert.NotNull(capturedChange);
        Assert.NotNull(capturedChange.Value.ReceivedTimestamp);
        Assert.Equal(externalSource, capturedChange.Value.Source);
    }

    [Fact]
    public async Task WhenApplyingNullItem_ThenItemIsSetToNull()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var source = new Person(context)
        {
            FirstName = "Parent",
            Father = new Person(context) { FirstName = "Father" }
        };
        var target = new Person(context)
        {
            FirstName = "Parent",
            Father = new Person(context) { FirstName = "Father" }
        };

        // Make a change - set Father to null
        var changes = new List<SubjectPropertyChange>();
        using (context.GetPropertyChangeObservable(System.Reactive.Concurrency.ImmediateScheduler.Instance)
            .Subscribe(c => changes.Add(c)))
        {
            source.Father = null;
        }

        // Act
        var update = SubjectUpdate.CreatePartialUpdateFromChanges(source, changes.ToArray(), []);
        await Verify(update);
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance);

        // Assert
        Assert.Null(target.Father);
    }

    [Fact]
    public async Task WhenApplyingAttributeUpdate_ThenAttributeIsUpdated()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var source = new CycleTestNode(context) { Name = "Node" };
        source.Name_Status = "updated";

        var target = new CycleTestNode(context) { Name = "Node" };

        // Act
        var update = SubjectUpdate.CreateCompleteUpdate(source, []);
        await Verify(update);
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance);

        // Assert
        Assert.Equal("updated", target.Name_Status);
    }

    [Fact]
    public void WhenApplyingUpdateWithEmptyRoot_ThenNothingHappens()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var target = new Person(context) { FirstName = "Original" };

        var update = new SubjectUpdate
        {
            Root = string.Empty,
            Subjects = new Dictionary<string, Dictionary<string, SubjectPropertyUpdate>>
            {
                ["1"] = new()
                {
                    ["FirstName"] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Value,
                        Value = "Changed"
                    }
                }
            }
        };

        // Act
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance);

        // Assert - nothing should change
        Assert.Equal("Original", target.FirstName);
    }

    [Fact]
    public void WhenApplyingUpdateWithMissingRootSubject_ThenNothingHappens()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var target = new Person(context) { FirstName = "Original" };

        var update = new SubjectUpdate
        {
            Root = "nonexistent",
            Subjects = new Dictionary<string, Dictionary<string, SubjectPropertyUpdate>>
            {
                ["1"] = new()
                {
                    ["FirstName"] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Value,
                        Value = "Changed"
                    }
                }
            }
        };

        // Act
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance);

        // Assert - nothing should change
        Assert.Equal("Original", target.FirstName);
    }

    [Fact]
    public void WhenApplyingCollectionUpdateWithInvalidIndex_ThenItIsIgnored()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var target = new Person(context)
        {
            FirstName = "Parent",
            Children =
            [
                new Person(context) { FirstName = "Child1" }
            ]
        };

        var update = new SubjectUpdate
        {
            Root = "1",
            Subjects = new Dictionary<string, Dictionary<string, SubjectPropertyUpdate>>
            {
                ["1"] = new()
                {
                    ["Children"] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Collection,
                        Operations =
                        [
                            new SubjectCollectionOperation
                            {
                                Action = SubjectCollectionOperationType.Remove,
                                Index = 999 // Invalid index - way out of bounds
                            }
                        ],
                        Count = 1
                    }
                }
            }
        };

        // Act - should not throw
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance);

        // Assert - collection unchanged
        Assert.Single(target.Children);
        Assert.Equal("Child1", target.Children[0].FirstName);
    }

    [Fact]
    public void WhenApplyingUpdateWithMissingSubjectId_ThenItIsIgnored()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var target = new Person(context) { FirstName = "Original" };

        var update = new SubjectUpdate
        {
            Root = "1",
            Subjects = new Dictionary<string, Dictionary<string, SubjectPropertyUpdate>>
            {
                ["1"] = new()
                {
                    ["Father"] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Item,
                        Id = "nonexistent" // References a subject that doesn't exist
                    }
                }
            }
        };

        // Act - should not throw
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance);

        // Assert - Father should remain null (not set to anything)
        Assert.Null(target.Father);
    }
}
