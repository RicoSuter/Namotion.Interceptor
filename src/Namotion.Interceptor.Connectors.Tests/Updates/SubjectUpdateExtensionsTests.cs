using System.Reactive.Linq;
using System.Text.Json;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.Registry;
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
                        Kind = SubjectPropertyUpdateKind.Object,
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
    public void WhenCreatingCompleteUpdateWithNullCollection_ThenUpdateHasKindValueAndValueNull()
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
        Assert.Equal(SubjectPropertyUpdateKind.Value, rootProps["Children"].Kind);
        Assert.Null(rootProps["Children"].Value);
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
}
