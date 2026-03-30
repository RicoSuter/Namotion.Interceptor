using System.Reactive.Linq;
using System.Text.Json;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Tests.Updates;

public partial class SubjectUpdateExtensionsTests
{
    [Fact]
    public async Task WhenApplyingSimpleProperty_ThenItWorks()
    {
        // Arrange
        var sourceContext = InterceptorSubjectContext.Create().WithRegistry();
        var targetContext = InterceptorSubjectContext.Create().WithRegistry();
        var source = new Person(sourceContext) { FirstName = "John", LastName = "Doe" };
        var target = new Person(targetContext);

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
        var sourceContext = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var targetContext = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var timestamp = new DateTimeOffset(2024, 1, 15, 10, 30, 0, TimeSpan.Zero);

        Person source;
        using (SubjectChangeContext.WithChangedTimestamp(timestamp))
        {
            source = new Person(sourceContext) { FirstName = "John", LastName = "Doe" };
        }

        var target = new Person(targetContext);

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
        var sourceContext = InterceptorSubjectContext.Create().WithRegistry();
        var targetContext = InterceptorSubjectContext.Create().WithRegistry();
        var source = new Person(sourceContext)
        {
            FirstName = "Child",
            Father = new Person(sourceContext) { FirstName = "Father" }
        };
        var target = new Person(targetContext);

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
        var sourceContext = InterceptorSubjectContext.Create().WithRegistry();
        var targetContext = InterceptorSubjectContext.Create().WithRegistry();
        var source = new Person(sourceContext)
        {
            FirstName = "Parent",
            Children =
            [
                new Person(sourceContext) { FirstName = "Child1" },
                new Person(sourceContext) { FirstName = "Child2" }
            ]
        };
        var target = new Person(targetContext);

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
        var sourceContext = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var targetContext = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var source = new Person(sourceContext) { FirstName = "Initial", LastName = "Name" };
        var target = new Person(targetContext);
        target.ApplySubjectUpdate(SubjectUpdate.CreateCompleteUpdate(source, []), DefaultSubjectFactory.Instance);

        // Make a change to source
        var changes = new List<SubjectPropertyChange>();
        using (sourceContext.GetPropertyChangeObservable(System.Reactive.Concurrency.ImmediateScheduler.Instance)
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
        var sourceContext = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var targetContext = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var source = new Person(sourceContext)
        {
            FirstName = "Parent",
            Children = [new Person(sourceContext) { FirstName = "ExistingChild" }]
        };
        var target = new Person(targetContext);
        target.ApplySubjectUpdate(SubjectUpdate.CreateCompleteUpdate(source, []), DefaultSubjectFactory.Instance);

        // Make a change - add a child
        var changes = new List<SubjectPropertyChange>();
        using (sourceContext.GetPropertyChangeObservable(System.Reactive.Concurrency.ImmediateScheduler.Instance)
            .Subscribe(c => changes.Add(c)))
        {
            source.Children = [..source.Children, new Person(sourceContext) { FirstName = "NewChild" }];
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
        var sourceContext = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var child1 = new Person(sourceContext) { FirstName = "Child1" };
        var child2 = new Person(sourceContext) { FirstName = "Child2" };
        var source = new Person(sourceContext) { FirstName = "Parent", Children = [child1, child2] };

        // Create target by applying a complete update (so IDs match)
        var targetContext = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var target = new Person(targetContext);
        target.ApplySubjectUpdate(SubjectUpdate.CreateCompleteUpdate(source, []), DefaultSubjectFactory.Instance);

        // Make a change - remove first child
        var changes = new List<SubjectPropertyChange>();
        using (sourceContext.GetPropertyChangeObservable(System.Reactive.Concurrency.ImmediateScheduler.Instance)
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
        var sourceContext = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var child1 = new Person(sourceContext) { FirstName = "Child1" };
        var child2 = new Person(sourceContext) { FirstName = "Child2" };
        var child3 = new Person(sourceContext) { FirstName = "Child3" };
        var source = new Person(sourceContext) { FirstName = "Parent", Children = [child1, child2, child3] };

        // Create target by applying a complete update (so IDs match)
        var targetContext = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var target = new Person(targetContext);
        target.ApplySubjectUpdate(SubjectUpdate.CreateCompleteUpdate(source, []), DefaultSubjectFactory.Instance);

        // Make a change - move child3 to front
        var changes = new List<SubjectPropertyChange>();
        using (sourceContext.GetPropertyChangeObservable(System.Reactive.Concurrency.ImmediateScheduler.Instance)
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
        var sourceContext = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var child = new Person(sourceContext) { FirstName = "OriginalChild" };
        var source = new Person(sourceContext) { FirstName = "Parent", Children = [child] };

        // Create target by applying a complete update (so IDs match for partial apply)
        var targetContext = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var target = new Person(targetContext);
        target.ApplySubjectUpdate(SubjectUpdate.CreateCompleteUpdate(source, []), DefaultSubjectFactory.Instance);

        // Make a change - update nested child's property
        var changes = new List<SubjectPropertyChange>();
        using (sourceContext.GetPropertyChangeObservable(System.Reactive.Concurrency.ImmediateScheduler.Instance)
            .Subscribe(c => changes.Add(c)))
        {
            child.FirstName = "UpdatedChild";
        }

        // Act
        var update = SubjectUpdate.CreatePartialUpdateFromChanges(source, changes.ToArray(), []);
        await Verify(update);
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance);

        // Assert
        Assert.Equal("UpdatedChild", target.Children[0].FirstName);
    }

    [Fact]
    public async Task WhenApplyingDictionaryComplete_ThenDictionaryIsPopulated()
    {
        // Arrange
        var sourceContext = InterceptorSubjectContext.Create().WithRegistry();
        var targetContext = InterceptorSubjectContext.Create().WithRegistry();
        var source = new CycleTestNode(sourceContext)
        {
            Name = "Root",
            Lookup = new Dictionary<string, CycleTestNode>
            {
                ["key1"] = new(sourceContext) { Name = "Item1" },
                ["key2"] = new(sourceContext) { Name = "Item2" }
            }
        };
        var target = new CycleTestNode(targetContext);

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
        var sourceContext = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var targetContext = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var existingItem = new CycleTestNode(sourceContext) { Name = "Existing" };
        var source = new CycleTestNode(sourceContext)
        {
            Name = "Root",
            Lookup = new Dictionary<string, CycleTestNode> { ["existing"] = existingItem }
        };
        var target = new CycleTestNode(targetContext);
        target.ApplySubjectUpdate(SubjectUpdate.CreateCompleteUpdate(source, []), DefaultSubjectFactory.Instance);

        // Make a change - add new key
        var changes = new List<SubjectPropertyChange>();
        using (sourceContext.GetPropertyChangeObservable(System.Reactive.Concurrency.ImmediateScheduler.Instance)
            .Subscribe(c => changes.Add(c)))
        {
            source.Lookup = new Dictionary<string, CycleTestNode>
            {
                ["existing"] = existingItem,
                ["newKey"] = new(sourceContext) { Name = "NewItem" }
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
        var sourceContext = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var targetContext = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var item1 = new CycleTestNode(sourceContext) { Name = "Item1" };
        var item2 = new CycleTestNode(sourceContext) { Name = "Item2" };
        var source = new CycleTestNode(sourceContext)
        {
            Name = "Root",
            Lookup = new Dictionary<string, CycleTestNode> { ["key1"] = item1, ["key2"] = item2 }
        };
        var target = new CycleTestNode(targetContext);
        target.ApplySubjectUpdate(SubjectUpdate.CreateCompleteUpdate(source, []), DefaultSubjectFactory.Instance);

        // Make a change - remove key1
        var changes = new List<SubjectPropertyChange>();
        using (sourceContext.GetPropertyChangeObservable(System.Reactive.Concurrency.ImmediateScheduler.Instance)
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
        var sourceContext = InterceptorSubjectContext.Create().WithRegistry();
        var targetContext = InterceptorSubjectContext.Create().WithRegistry();
        var parent = new CycleTestNode(sourceContext) { Name = "Parent" };
        var child = new CycleTestNode(sourceContext) { Name = "Child", Parent = parent };
        parent.Child = child;

        var target = new CycleTestNode(targetContext);

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
        var sourceContext = InterceptorSubjectContext.Create().WithRegistry();
        var targetContext = InterceptorSubjectContext.Create().WithRegistry();
        var node = new CycleTestNode(sourceContext) { Name = "SelfRef" };
        node.Self = node;

        var target = new CycleTestNode(targetContext);

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
                ["1"] = new()
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
        var sourceContext = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var targetContext = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var source = new Person(sourceContext) { FirstName = "John" };
        var target = new Person(targetContext);
        var externalSource = new object();

        // Capture FirstName change specifically (FullName is derived and fires without source)
        SubjectPropertyChange? capturedChange = null;
        using var subscription = targetContext
            .GetPropertyChangeObservable(System.Reactive.Concurrency.ImmediateScheduler.Instance)
            .Where(c => c.Property.Name == "FirstName")
            .Subscribe(c => capturedChange = c);

        // Act
        var update = SubjectUpdate.CreateCompleteUpdate(source, []);
        await Verify(update);
        using (SubjectChangeContext.WithSource(externalSource))
        {
            target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance);
        }

        // Assert
        Assert.NotNull(capturedChange);
        Assert.Equal(externalSource, capturedChange.Value.Source);
    }

    [Fact]
    public async Task WhenApplyingNullItem_ThenItemIsSetToNull()
    {
        // Arrange
        var sourceContext = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var targetContext = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var source = new Person(sourceContext)
        {
            FirstName = "Parent",
            Father = new Person(sourceContext) { FirstName = "Father" }
        };
        var target = new Person(targetContext);
        target.ApplySubjectUpdate(SubjectUpdate.CreateCompleteUpdate(source, []), DefaultSubjectFactory.Instance);

        // Make a change - set Father to null
        var changes = new List<SubjectPropertyChange>();
        using (sourceContext.GetPropertyChangeObservable(System.Reactive.Concurrency.ImmediateScheduler.Instance)
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
        var sourceContext = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var targetContext = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var source = new CycleTestNode(sourceContext) { Name = "Node" };
        source.Name_Status = "updated";

        var target = new CycleTestNode(targetContext) { Name = "Node" };

        // Act
        var update = SubjectUpdate.CreateCompleteUpdate(source, []);
        await Verify(update);
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance);

        // Assert
        Assert.Equal("updated", target.Name_Status);
    }

    [Fact]
    public void WhenApplyingUpdateWithNullRoot_ThenNothingHappens()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var target = new Person(context) { FirstName = "Original" };

        var update = new SubjectUpdate
        {
            Root = null,
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

        // Assert - nothing should change (no registry to resolve stable IDs)
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
    public void WhenApplyingUpdateWithMissingSubjectId_ThenSubjectIsCreatedForLaterPropertyUpdates()
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
                        Kind = SubjectPropertyUpdateKind.Object,
                        Id = "new-father" // References a subject whose properties may arrive in a later batch
                    }
                }
            }
        };

        // Act - should not throw
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance);

        // Assert - Father should be created so future value updates can find it by ID
        Assert.NotNull(target.Father);
        Assert.Equal("new-father", ((IInterceptorSubject)target.Father).TryGetSubjectId());
    }


    [Fact]
    public void WhenApplyingCompleteCollectionWithNewItem_ThenItemIsAppended()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var target = new Person(context)
        {
            FirstName = "Parent",
            Children = [new Person(context) { FirstName = "Child1" }] // 1 item
        };

        // Create a complete collection update with two items (existing + new)
        var child1Id = "child1_stable_id_test01";
        var child2Id = "child2_stable_id_test02";
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
                        Items =
                        [
                            new SubjectPropertyItemUpdate { Id = child1Id },
                            new SubjectPropertyItemUpdate { Id = child2Id }
                        ]
                    }
                },
                [child1Id] = new()
                {
                    ["FirstName"] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Value,
                        Value = "Child1"
                    }
                },
                [child2Id] = new()
                {
                    ["FirstName"] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Value,
                        Value = "NewChild"
                    }
                }
            }
        };

        // Act
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance);

        // Assert
        Assert.Equal(2, target.Children.Count);
        Assert.Equal("Child1", target.Children[0].FirstName);
        Assert.Equal("NewChild", target.Children[1].FirstName);
    }



    [Fact]
    public void WhenApplyingCompleteStateToEmptyCollection_ThenItemIsAdded()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var target = new Person(context)
        {
            FirstName = "Parent",
            Children = [] // Empty collection
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
                        Items =
                        [
                            new SubjectPropertyItemUpdate { Id = "2" }
                        ]
                    }
                },
                ["2"] = new()
                {
                    ["FirstName"] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Value,
                        Value = "NewChild"
                    }
                }
            }
        };

        // Act
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance);

        // Assert
        Assert.Single(target.Children);
        Assert.Equal("NewChild", target.Children[0].FirstName);
    }

    [Fact]
    public void WhenApplyingNullCollection_ThenCollectionIsSetToNull()
    {
        // Arrange
        var sourceContext = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var targetContext = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var source = new Person(sourceContext) { FirstName = "Parent", Children = [new Person(sourceContext) { FirstName = "Child" }] };
        var target = new Person(targetContext);
        target.ApplySubjectUpdate(SubjectUpdate.CreateCompleteUpdate(source, []), DefaultSubjectFactory.Instance);

        // Make a change - set collection to null
        var changes = new List<SubjectPropertyChange>();
        using (sourceContext.GetPropertyChangeObservable(System.Reactive.Concurrency.ImmediateScheduler.Instance)
            .Subscribe(c => changes.Add(c)))
        {
            source.Children = null!;
        }

        // Act
        var update = SubjectUpdate.CreatePartialUpdateFromChanges(source, changes.ToArray(), []);
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance);

        // Assert
        Assert.Null(target.Children);
    }

    [Fact]
    public void WhenApplyingNullDictionary_ThenDictionaryIsSetToNull()
    {
        // Arrange
        var sourceContext = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var targetContext = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var source = new CycleTestNode(sourceContext)
        {
            Name = "Root",
            Lookup = new Dictionary<string, CycleTestNode> { ["key1"] = new(sourceContext) { Name = "Item1" } }
        };
        var target = new CycleTestNode(targetContext);
        target.ApplySubjectUpdate(SubjectUpdate.CreateCompleteUpdate(source, []), DefaultSubjectFactory.Instance);

        // Make a change - set dictionary to null
        var changes = new List<SubjectPropertyChange>();
        using (sourceContext.GetPropertyChangeObservable(System.Reactive.Concurrency.ImmediateScheduler.Instance)
            .Subscribe(c => changes.Add(c)))
        {
            source.Lookup = null!;
        }

        // Act
        var update = SubjectUpdate.CreatePartialUpdateFromChanges(source, changes.ToArray(), []);
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance);

        // Assert
        Assert.Null(target.Lookup);
    }

    [Fact]
    public void WhenCreatingCompleteUpdateWithNullCollection_ThenUpdateHasCollectionKindWithNullItems()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var source = new Person(context) { FirstName = "Parent", Children = null! };

        // Act
        var update = SubjectUpdate.CreateCompleteUpdate(source, []);

        // Assert
        Assert.NotNull(update.Root);
        var rootProps = update.Subjects[update.Root!];
        Assert.True(rootProps.ContainsKey("Children"));
        Assert.Equal(SubjectPropertyUpdateKind.Collection, rootProps["Children"].Kind);
        Assert.Null(rootProps["Children"].Items);
    }

    [InterceptorSubject]
    public partial class IntKeyNode
    {
        public IntKeyNode()
        {
            Name = "";
            IntLookup = new Dictionary<int, CycleTestNode>();
        }

        public partial string Name { get; set; }
        public partial Dictionary<int, CycleTestNode> IntLookup { get; set; }
    }

    [Fact]
    public void WhenApplyingDictionaryUpdateWithIntKeys_ThenExistingEntriesAreMatched()
    {
        // Arrange
        var sourceContext = InterceptorSubjectContext.Create().WithRegistry();
        var targetContext = InterceptorSubjectContext.Create().WithRegistry();
        var source = new IntKeyNode(sourceContext)
        {
            Name = "Root",
            IntLookup = new Dictionary<int, CycleTestNode>
            {
                [1] = new(sourceContext) { Name = "Item1" },
                [2] = new(sourceContext) { Name = "Item2" }
            }
        };
        var target = new IntKeyNode(targetContext);

        // Act - complete update round-trip (values will be int keys in source, need to be matched after deserialization)
        var update = SubjectUpdate.CreateCompleteUpdate(source, []);
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance);

        // Assert
        Assert.Equal(2, target.IntLookup.Count);
        Assert.Equal("Item1", target.IntLookup[1].Name);
        Assert.Equal("Item2", target.IntLookup[2].Name);
    }

    [Fact]
    public void WhenApplyingDictionaryItemPropertyChangeViaJsonRoundTrip_ThenUpdateIsAppliedCorrectly()
    {
        // Arrange - when a property on a dictionary item changes, the partial update
        // only contains an entry for the changed item subject. After JSON round-trip,
        // the target must have matching stable IDs (via complete update initialization)
        // so the applier can find the correct subject via stable ID lookup.
        var sourceContext = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var item1 = new CycleTestNode(sourceContext) { Name = "Item1" };
        var source = new CycleTestNode(sourceContext)
        {
            Name = "Root",
            Lookup = new Dictionary<string, CycleTestNode> { ["myKey"] = item1 }
        };

        // Create target by applying a complete update (so IDs match)
        var targetContext = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var target = new CycleTestNode(targetContext);
        target.ApplySubjectUpdate(SubjectUpdate.CreateCompleteUpdate(source, []), DefaultSubjectFactory.Instance);

        // Change a property on the dictionary item
        var changes = new List<SubjectPropertyChange>();
        using (sourceContext.GetPropertyChangeObservable(System.Reactive.Concurrency.ImmediateScheduler.Instance)
            .Subscribe(c => changes.Add(c)))
        {
            item1.Name = "Item1Updated";
        }

        // Create the update, serialize to JSON, then deserialize (simulating WebSocket transfer)
        var update = SubjectUpdate.CreatePartialUpdateFromChanges(source, changes.ToArray(), []);
        var json = JsonSerializer.Serialize(update);
        var deserialized = JsonSerializer.Deserialize<SubjectUpdate>(json)!;

        // Act - apply the deserialized update; the applier uses stable ID lookup
        // to find the target item since the root entry is not in Subjects
        target.ApplySubjectUpdate(deserialized, DefaultSubjectFactory.Instance);

        // Assert
        Assert.Equal("Item1Updated", target.Lookup["myKey"].Name);
    }

    [Fact]
    public void WhenApplyingDictionaryPartialUpdateWithIntKeys_ThenExistingEntriesAreMatchedAndNewKeysAdded()
    {
        // Arrange
        var sourceContext = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var targetContext = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var existingItem = new CycleTestNode(sourceContext) { Name = "Existing" };
        var source = new IntKeyNode(sourceContext)
        {
            Name = "Root",
            IntLookup = new Dictionary<int, CycleTestNode> { [1] = existingItem }
        };
        var target = new IntKeyNode(targetContext);
        target.ApplySubjectUpdate(SubjectUpdate.CreateCompleteUpdate(source, []), DefaultSubjectFactory.Instance);

        // Make a change - add key 2 and update key 1's name
        var changes = new List<SubjectPropertyChange>();
        using (sourceContext.GetPropertyChangeObservable(System.Reactive.Concurrency.ImmediateScheduler.Instance)
            .Subscribe(c => changes.Add(c)))
        {
            existingItem.Name = "Updated";
            source.IntLookup = new Dictionary<int, CycleTestNode>
            {
                [1] = existingItem,
                [2] = new(sourceContext) { Name = "NewItem" }
            };
        }

        // Act
        var update = SubjectUpdate.CreatePartialUpdateFromChanges(source, changes.ToArray(), []);
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance);

        // Assert
        Assert.Equal(2, target.IntLookup.Count);
        Assert.Equal("Updated", target.IntLookup[1].Name);
        Assert.Equal("NewItem", target.IntLookup[2].Name);
    }

    [Fact]
    public void WhenObjectRefPropertiesArriveInLaterBatch_ThenSubjectIsCreatedAndLaterUpdateApplied()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var target = new Person(context) { FirstName = "Parent" };

        // Batch 1: Structural update — ObjectRef points to new subject, but NO properties for it
        var batch1 = new SubjectUpdate
        {
            Root = "root",
            Subjects = new Dictionary<string, Dictionary<string, SubjectPropertyUpdate>>
            {
                ["root"] = new()
                {
                    ["Father"] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Object,
                        Id = "father-1"
                    }
                }
                // Note: no entry for "father-1" — properties arrive in next batch
            }
        };

        target.ApplySubjectUpdate(batch1, DefaultSubjectFactory.Instance);

        // Assert: Father created with default values, ID registered
        Assert.NotNull(target.Father);
        Assert.Equal("father-1", ((IInterceptorSubject)target.Father).TryGetSubjectId());
        Assert.Null(target.Father.FirstName); // defaults

        // Batch 2: Value-only update for the same subject
        var batch2 = new SubjectUpdate
        {
            Root = "root",
            Subjects = new Dictionary<string, Dictionary<string, SubjectPropertyUpdate>>
            {
                ["father-1"] = new()
                {
                    ["FirstName"] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Value,
                        Value = "John"
                    }
                }
            }
        };

        target.ApplySubjectUpdate(batch2, DefaultSubjectFactory.Instance);

        // Assert: Value update applied to the same subject
        Assert.NotNull(target.Father);
        Assert.Equal("John", target.Father.FirstName);
        Assert.Equal("father-1", ((IInterceptorSubject)target.Father).TryGetSubjectId());
    }

    [Fact]
    public void WhenCollectionItemPropertiesArriveInLaterBatch_ThenItemIsCreatedAndLaterUpdateApplied()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var target = new Person(context) { FirstName = "Parent", Children = [] };

        // Batch 1: Collection update with new item, but NO properties for it
        var batch1 = new SubjectUpdate
        {
            Root = "root",
            Subjects = new Dictionary<string, Dictionary<string, SubjectPropertyUpdate>>
            {
                ["root"] = new()
                {
                    ["Children"] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Collection,
                        Items = [new SubjectPropertyItemUpdate { Id = "child-1" }]
                    }
                }
                // Note: no entry for "child-1"
            }
        };

        target.ApplySubjectUpdate(batch1, DefaultSubjectFactory.Instance);

        // Assert: Child created with defaults, ID registered
        Assert.Single(target.Children);
        Assert.Equal("child-1", ((IInterceptorSubject)target.Children[0]).TryGetSubjectId());

        // Batch 2: Value update for the child
        var batch2 = new SubjectUpdate
        {
            Root = "root",
            Subjects = new Dictionary<string, Dictionary<string, SubjectPropertyUpdate>>
            {
                ["child-1"] = new()
                {
                    ["FirstName"] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Value,
                        Value = "Alice"
                    }
                }
            }
        };

        target.ApplySubjectUpdate(batch2, DefaultSubjectFactory.Instance);

        // Assert: Value applied to same child
        Assert.Single(target.Children);
        Assert.Equal("Alice", target.Children[0].FirstName);
        Assert.Equal("child-1", ((IInterceptorSubject)target.Children[0]).TryGetSubjectId());
    }

    [Fact]
    public void WhenObjectRefAndPropertiesInSameBatch_ThenSubjectIsFullyPopulated()
    {
        // Arrange — the common case where structural + properties arrive together
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var target = new Person(context) { FirstName = "Parent" };

        var update = new SubjectUpdate
        {
            Root = "root",
            Subjects = new Dictionary<string, Dictionary<string, SubjectPropertyUpdate>>
            {
                ["root"] = new()
                {
                    ["Father"] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Object,
                        Id = "father-1"
                    }
                },
                ["father-1"] = new()
                {
                    ["FirstName"] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Value,
                        Value = "Bob"
                    }
                }
            }
        };

        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance);

        // Assert: Subject created and fully populated in one batch
        Assert.NotNull(target.Father);
        Assert.Equal("Bob", target.Father.FirstName);
        Assert.Equal("father-1", ((IInterceptorSubject)target.Father).TryGetSubjectId());
    }

    [Fact]
    public void WhenObjectRefNotFoundAndNotComplete_ThenSkipsCreation()
    {
        // Arrange: partial update references a subject by ID that doesn't exist locally
        // and is NOT in CompleteSubjectIds (sender considered it "existing")
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var target = new Person(context) { FirstName = "Parent" };

        var update = new SubjectUpdate
        {
            Root = "root",
            Subjects = new Dictionary<string, Dictionary<string, SubjectPropertyUpdate>>
            {
                ["root"] = new()
                {
                    ["Father"] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Object,
                        Id = "unknown-subject-id"
                    }
                }
            },
            CompleteSubjectIds = [] // empty set = no subjects are complete
        };

        // Act
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance);

        // Assert: Father should remain null — the unknown subject was skipped, not created with defaults
        Assert.Null(target.Father);
    }

    [Fact]
    public void WhenObjectRefNotFoundButIsComplete_ThenCreatesSubject()
    {
        // Arrange: partial update references a subject with complete state
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var target = new Person(context) { FirstName = "Parent" };

        var update = new SubjectUpdate
        {
            Root = "root",
            Subjects = new Dictionary<string, Dictionary<string, SubjectPropertyUpdate>>
            {
                ["root"] = new()
                {
                    ["Father"] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Object,
                        Id = "new-father"
                    }
                },
                ["new-father"] = new()
                {
                    ["FirstName"] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Value,
                        Value = "John"
                    }
                }
            },
            CompleteSubjectIds = ["new-father"] // explicitly marked as complete
        };

        // Act
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance);

        // Assert: Father created and populated
        Assert.NotNull(target.Father);
        Assert.Equal("John", target.Father.FirstName);
    }

    [Fact]
    public void WhenObjectRefNotFoundAndCompleteSubjectIdsNull_ThenCreatesSubject()
    {
        // Arrange: null CompleteSubjectIds means "all subjects are complete" (e.g., complete update)
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var target = new Person(context) { FirstName = "Parent" };

        var update = new SubjectUpdate
        {
            Root = "root",
            Subjects = new Dictionary<string, Dictionary<string, SubjectPropertyUpdate>>
            {
                ["root"] = new()
                {
                    ["Father"] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Object,
                        Id = "new-father"
                    }
                }
            },
            CompleteSubjectIds = null // null = all complete (backward compatible)
        };

        // Act
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance);

        // Assert: Father created (null means all complete)
        Assert.NotNull(target.Father);
    }

    [Fact]
    public void WhenCollectionItemNotFoundAndNotComplete_ThenSkipsItem()
    {
        // Arrange: collection references a subject that doesn't exist locally and isn't complete
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var target = new Person(context) { FirstName = "Parent", Children = [] };

        var update = new SubjectUpdate
        {
            Root = "root",
            Subjects = new Dictionary<string, Dictionary<string, SubjectPropertyUpdate>>
            {
                ["root"] = new()
                {
                    ["Children"] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Collection,
                        Items =
                        [
                            new SubjectPropertyItemUpdate { Id = "unknown-child" }
                        ]
                    }
                }
            },
            CompleteSubjectIds = [] // not complete
        };

        // Act
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance);

        // Assert: collection should be empty — unknown item was skipped
        Assert.Empty(target.Children);
    }

    [Fact]
    public void WhenDictionaryItemNotFoundAndNotComplete_ThenSkipsItem()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var target = new Person(context) { FirstName = "Root" };

        var update = new SubjectUpdate
        {
            Root = "root",
            Subjects = new Dictionary<string, Dictionary<string, SubjectPropertyUpdate>>
            {
                ["root"] = new()
                {
                    ["Relationships"] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Dictionary,
                        Items =
                        [
                            new SubjectPropertyItemUpdate { Id = "unknown-item", Key = "friend" }
                        ]
                    }
                }
            },
            CompleteSubjectIds = [] // not complete
        };

        // Act
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance);

        // Assert: dictionary should be empty — unknown item was skipped
        Assert.NotNull(target.Relationships);
        Assert.Empty(target.Relationships);
    }

    [Fact]
    public void WhenPartialUpdateFromChanges_ThenCompleteSubjectIdsIsPopulated()
    {
        // Arrange: create a model and make changes that trigger ProcessSubjectComplete
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var root = new Person(context) { FirstName = "Root" };

        var changes = new List<SubjectPropertyChange>();
        using (context.GetPropertyChangeObservable(System.Reactive.Concurrency.ImmediateScheduler.Instance)
            .Subscribe(c => changes.Add(c)))
        {
            // Structural change — adds a new subject that needs ProcessSubjectComplete
            root.Father = new Person(context) { FirstName = "Father" };
        }

        // Act
        var update = SubjectUpdate.CreatePartialUpdateFromChanges(root, changes.ToArray(), []);

        // Assert: CompleteSubjectIds should be non-null and contain the new subject's ID
        Assert.NotNull(update.CompleteSubjectIds);
        Assert.NotEmpty(update.CompleteSubjectIds);

        var fatherId = ((IInterceptorSubject)root.Father).TryGetSubjectId()!;
        Assert.Contains(fatherId, update.CompleteSubjectIds);
    }

    [Fact]
    public void WhenCompleteUpdate_ThenCompleteSubjectIdsIsNull()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var root = new Person(context) { FirstName = "Root", Father = new Person(context) { FirstName = "Father" } };

        // Act
        var update = SubjectUpdate.CreateCompleteUpdate(root, []);

        // Assert: null means "all subjects are complete"
        Assert.Null(update.CompleteSubjectIds);
    }

    /// <summary>
    /// Reproduces the "no-parents" registry leak during concurrent ApplySubjectUpdate
    /// (simulating WebSocket Welcome apply) and direct property writes (simulating
    /// MutationEngine). Thread A applies updates that replace the child subject,
    /// Thread B writes grandchildren to the child's structural property.
    /// The registry re-registers the child as a parent side-effect after it was
    /// detached by the update apply, leaving it orphaned with refCount=0, no-parents.
    /// </summary>
    [Fact]
    [Trait("Category", "Concurrency")]
    public void ConcurrentApplySubjectUpdateAndPropertyWrite_NoOrphanedSubjectsInRegistry()
    {
        const int rounds = 10;
        const int iterationsPerThread = 2000;
        var totalOrphaned = 0;

        for (var round = 0; round < rounds; round++)
        {
            var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
            var registry = context.TryGetService<ISubjectRegistry>()!;
            var target = new Person(context) { FirstName = "Root" };

            // Give the root a child with a known ID
            var child = new Person { FirstName = "Child" };
            target.Father = child;

            var barrier = new Barrier(2);
            var idCounter = 0;

            // Thread A: repeatedly applies SubjectUpdate that replaces Father
            // (simulates WebSocket Welcome apply replacing the graph)
            var updateThread = new Thread(() =>
            {
                barrier.SignalAndWait();
                for (var i = 0; i < iterationsPerThread; i++)
                {
                    var newId = $"child-{Interlocked.Increment(ref idCounter)}";
                    var update = new SubjectUpdate
                    {
                        Root = "root",
                        Subjects = new Dictionary<string, Dictionary<string, SubjectPropertyUpdate>>
                        {
                            ["root"] = new()
                            {
                                ["Father"] = new SubjectPropertyUpdate
                                {
                                    Kind = SubjectPropertyUpdateKind.Object,
                                    Id = newId
                                }
                            },
                            [newId] = new()
                            {
                                ["FirstName"] = new SubjectPropertyUpdate
                                {
                                    Kind = SubjectPropertyUpdateKind.Value,
                                    Value = $"NewChild_{i}"
                                }
                            }
                        }
                    };

                    target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance);
                }
            });
            updateThread.IsBackground = true;

            // Thread B: writes grandchild subjects to the current child's Mother property
            // (simulates MutationEngine writing to subjects in the graph)
            var writeThread = new Thread(() =>
            {
                barrier.SignalAndWait();
                for (var i = 0; i < iterationsPerThread; i++)
                {
                    try
                    {
                        var currentChild = target.Father;
                        if (currentChild is not null)
                        {
                            currentChild.Mother = new Person { FirstName = $"Grandchild_{i}" };
                            currentChild.Mother = null;
                        }
                    }
                    catch
                    {
                        // Ignore races (property access on detached subject)
                    }
                }
            });
            writeThread.IsBackground = true;

            updateThread.Start();
            writeThread.Start();
            updateThread.Join();
            writeThread.Join();

            // Clean up
            target.Father = null;

            // Count orphans (anything besides the root)
            var knownSubjects = registry.KnownSubjects;
            foreach (var kvp in knownSubjects)
            {
                if (!ReferenceEquals(kvp.Key, target))
                {
                    var name = ((Person)kvp.Key).FirstName;
                    var refCount = kvp.Value.ReferenceCount;
                    var parents = kvp.Value.Parents;
                    var parentDesc = parents.Length > 0 ? "has-parents" : "no-parents";
                    Console.WriteLine($"  Round {round}: orphan '{name}' refCount={refCount} {parentDesc}");
                    totalOrphaned++;
                }
            }
        }

        Assert.True(
            totalOrphaned == 0,
            $"Detected {totalOrphaned} total orphaned subject(s) across {rounds} rounds. " +
            $"This indicates a registry leak during concurrent ApplySubjectUpdate " +
            $"(Welcome apply) and direct property writes (MutationEngine).");
    }

    /// <summary>
    /// Reproduces the ConnectorTester "no-parents" leak by simulating WebSocket
    /// reconnection: complete graph replacement via ApplySubjectUpdate while
    /// MutationEngine concurrently writes to subjects that may be in the process
    /// of being replaced. Uses complete updates (like Welcome) that replace the
    /// entire graph structure.
    /// </summary>
    [Fact]
    [Trait("Category", "Concurrency")]
    public void ConcurrentCompleteUpdateAndStructuralWrites_NoOrphanedSubjectsInRegistry()
    {
        const int rounds = 10;
        const int iterationsPerThread = 1000;
        var totalOrphaned = 0;

        for (var round = 0; round < rounds; round++)
        {
            var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
            var registry = context.TryGetService<ISubjectRegistry>()!;
            var root = new Person(context) { FirstName = "Root" };

            // Set up initial graph: root → child → grandchild
            root.Father = new Person { FirstName = "Child0" };
            root.Father.Mother = new Person { FirstName = "Grandchild0" };

            var barrier = new Barrier(2);
            var idCounter = 0;

            // Thread A: repeatedly applies COMPLETE SubjectUpdate that replaces
            // the entire graph (simulating Welcome apply during reconnection)
            var updateThread = new Thread(() =>
            {
                barrier.SignalAndWait();
                for (var i = 0; i < iterationsPerThread; i++)
                {
                    var childId = $"child-{Interlocked.Increment(ref idCounter)}";
                    var grandchildId = $"gc-{Interlocked.Increment(ref idCounter)}";

                    // Complete update: replaces root's Father AND the child's Mother
                    var update = SubjectUpdate.CreateCompleteUpdate(root, []);

                    // Apply a fresh complete update that replaces everything
                    var freshUpdate = new SubjectUpdate
                    {
                        Root = "root",
                        Subjects = new Dictionary<string, Dictionary<string, SubjectPropertyUpdate>>
                        {
                            ["root"] = new()
                            {
                                ["Father"] = new SubjectPropertyUpdate
                                {
                                    Kind = SubjectPropertyUpdateKind.Object,
                                    Id = childId
                                }
                            },
                            [childId] = new()
                            {
                                ["FirstName"] = new SubjectPropertyUpdate { Kind = SubjectPropertyUpdateKind.Value, Value = $"Child_{i}" },
                                ["Mother"] = new SubjectPropertyUpdate
                                {
                                    Kind = SubjectPropertyUpdateKind.Object,
                                    Id = grandchildId
                                }
                            },
                            [grandchildId] = new()
                            {
                                ["FirstName"] = new SubjectPropertyUpdate { Kind = SubjectPropertyUpdateKind.Value, Value = $"GC_{i}" }
                            }
                        }
                    };

                    root.ApplySubjectUpdate(freshUpdate, DefaultSubjectFactory.Instance);
                }
            });
            updateThread.IsBackground = true;

            // Thread B: concurrently writes to structural properties of current subjects
            // (simulates MutationEngine modifying the graph during reconnection)
            var writeThread = new Thread(() =>
            {
                barrier.SignalAndWait();
                for (var i = 0; i < iterationsPerThread; i++)
                {
                    try
                    {
                        var father = root.Father;
                        if (father is not null)
                        {
                            // Write to the child's structural property
                            father.Mother = new Person { FirstName = $"MutGC_{i}" };
                            father.Mother = null;

                            // Also write to the child's collection
                            father.Children = [new Person { FirstName = $"MutChild_{i}" }];
                            father.Children = [];
                        }
                    }
                    catch
                    {
                        // Ignore races
                    }
                }
            });
            writeThread.IsBackground = true;

            updateThread.Start();
            writeThread.Start();
            updateThread.Join();
            writeThread.Join();

            // Clean up
            try
            {
                if (root.Father is not null)
                {
                    root.Father.Mother = null;
                    root.Father.Children = [];
                }
                root.Father = null;
            }
            catch { }

            var knownSubjects = registry.KnownSubjects;
            foreach (var kvp in knownSubjects)
            {
                if (!ReferenceEquals(kvp.Key, root))
                {
                    var name = ((Person)kvp.Key).FirstName ?? "?";
                    var refCount = kvp.Value.ReferenceCount;
                    var parents = kvp.Value.Parents;
                    var parentDesc = parents.Length > 0 ? "has-parents" : "no-parents";
                    Console.WriteLine($"  Round {round}: orphan '{name}' refCount={refCount} {parentDesc}");
                    totalOrphaned++;
                }
            }
        }

        Assert.True(
            totalOrphaned == 0,
            $"Detected {totalOrphaned} total orphaned subject(s) across {rounds} rounds. " +
            $"This indicates a registry leak during concurrent complete graph replacement " +
            $"(Welcome apply) and structural property writes (MutationEngine).");
    }
}
