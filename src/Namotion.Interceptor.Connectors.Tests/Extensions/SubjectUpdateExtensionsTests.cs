using System.Text.Json;
using Moq;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Tests.Extensions;

public class SubjectUpdateExtensionsTests
{
    [Fact]
    public void WhenApplyingSimpleProperty_ThenItWorks()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();
        
        var person = new Person(context);
        
        // Act
        person.ApplySubjectUpdate(new SubjectUpdate
        {
            Properties = new Dictionary<string, SubjectPropertyUpdate>
            {
                {
                    nameof(Person.FirstName), SubjectPropertyUpdate.Create("John")
                },
                {
                    nameof(Person.LastName), SubjectPropertyUpdate.Create("Doe")
                }
            }
        }, DefaultSubjectFactory.Instance);
        
        // Assert
        Assert.Equal("John", person.FirstName);
        Assert.Equal("Doe", person.LastName);
    }
    
    [Fact]
    public void WhenApplyingSimplePropertyWithTimestamp_ThenTimestampIsPreserved()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var person = new Person(context);

        var timestamp = DateTimeOffset.UtcNow.AddDays(-200);
        
        // Act
        person.ApplySubjectUpdate(new SubjectUpdate
        {
            Properties = new Dictionary<string, SubjectPropertyUpdate>
            {
                {
                    nameof(Person.FirstName), SubjectPropertyUpdate.Create("John", timestamp)
                },
                {
                    nameof(Person.LastName), SubjectPropertyUpdate.Create("Doe", timestamp)
                }
            }
        }, DefaultSubjectFactory.Instance);
        
        // Assert
        Assert.Equal(timestamp, person
            .GetPropertyReference("FirstName")
            .TryGetWriteTimestamp());
    }
    
    [Fact]
    public void WhenApplyingNestedProperty_ThenItWorks()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var person = new Person(context);
        
        // Act
        person.ApplySubjectUpdate(new SubjectUpdate
        {
            Properties = new Dictionary<string, SubjectPropertyUpdate>
            {
                {
                    nameof(Person.Father), SubjectPropertyUpdate.Create(new SubjectUpdate
                    {
                        Properties = new Dictionary<string, SubjectPropertyUpdate>
                        {
                            {
                                nameof(Person.FirstName), SubjectPropertyUpdate.Create("John")
                            }
                        }
                    })
                }
            }
        }, DefaultSubjectFactory.Instance);
        
        // Assert
        Assert.Equal("John", person.Father?.FirstName);
    }
    
    [Fact]
    public void WhenApplyingCollectionProperty_ThenItWorks()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var person = new Person(context);

        // Act - Use Operations.Insert for creating new items
        person.ApplySubjectUpdate(new SubjectUpdate
        {
            Properties = new Dictionary<string, SubjectPropertyUpdate>
            {
                {
                    nameof(Person.Children),
                    new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Collection,
                        Operations =
                        [
                            new SubjectCollectionOperation
                            {
                                Action = SubjectCollectionOperationType.Insert,
                                Index = 0,
                                Item = new SubjectUpdate
                                {
                                    Properties = new Dictionary<string, SubjectPropertyUpdate>
                                    {
                                        { nameof(Person.FirstName), SubjectPropertyUpdate.Create("John") }
                                    }
                                }
                            },
                            new SubjectCollectionOperation
                            {
                                Action = SubjectCollectionOperationType.Insert,
                                Index = 1,
                                Item = new SubjectUpdate
                                {
                                    Properties = new Dictionary<string, SubjectPropertyUpdate>
                                    {
                                        { nameof(Person.FirstName), SubjectPropertyUpdate.Create("Anna") }
                                    }
                                }
                            }
                        ]
                    }
                }
            }
        }, DefaultSubjectFactory.Instance);

        // Assert
        Assert.Equal("John", person.Children.First().FirstName);
        Assert.Equal("Anna", person.Children.Last().FirstName);
    }

    [Fact]
    public void WhenApplyingFromSourceWithTransform_ThenTransformIsApplied()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var person = new Person(context);
        var sourceMock = new Mock<ISubjectSource>();
        var transformCalled = false;

        // Act
        person.ApplySubjectUpdateFromSource(
            new SubjectUpdate
            {
                Properties = new Dictionary<string, SubjectPropertyUpdate>
                {
                    {
                        nameof(Person.FirstName), SubjectPropertyUpdate.Create("John")
                    }
                }
            }, 
            sourceMock.Object, 
            DefaultSubjectFactory.Instance,
            (_, update) =>
            {
                transformCalled = true;
                update.Value = "Transformed";
            });

        // Assert
        Assert.True(transformCalled);
        Assert.Equal("Transformed", person.FirstName);
    }

    [Fact]
    public void WhenApplyingCollectionUpdateInPlace_ThenExistingItemsAreUpdatedWithoutReplacement()
    {
        // Arrange - Create person with existing children
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var person = new Person(context);
        var child1 = new Person(context) { FirstName = "Child1" };
        var child2 = new Person(context) { FirstName = "Child2" };
        person.Children = [child1, child2];

        // Act - Apply update to existing items only (no structural change)
        person.ApplySubjectUpdate(new SubjectUpdate
        {
            Properties = new Dictionary<string, SubjectPropertyUpdate>
            {
                {
                    nameof(Person.Children),
                    SubjectPropertyUpdate.Create(
                        new SubjectPropertyCollectionUpdate
                        {
                            Index = 0,
                            Item = new SubjectUpdate
                            {
                                Properties = new Dictionary<string, SubjectPropertyUpdate>
                                {
                                    { nameof(Person.FirstName), SubjectPropertyUpdate.Create("UpdatedChild1") }
                                }
                            }
                        },
                        new SubjectPropertyCollectionUpdate
                        {
                            Index = 1,
                            Item = new SubjectUpdate
                            {
                                Properties = new Dictionary<string, SubjectPropertyUpdate>
                                {
                                    { nameof(Person.FirstName), SubjectPropertyUpdate.Create("UpdatedChild2") }
                                }
                            }
                        })
                }
            }
        }, DefaultSubjectFactory.Instance);

        // Assert - Same instances should be reused, values updated
        Assert.Same(child1, person.Children[0]);
        Assert.Same(child2, person.Children[1]);
        Assert.Equal("UpdatedChild1", person.Children[0].FirstName);
        Assert.Equal("UpdatedChild2", person.Children[1].FirstName);
    }

    [Fact]
    public void WhenApplyingSparseCollectionUpdate_ThenOnlySpecifiedIndexesAreUpdated()
    {
        // Arrange - Create person with 3 children
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var person = new Person(context);
        var child0 = new Person(context) { FirstName = "Child0" };
        var child1 = new Person(context) { FirstName = "Child1" };
        var child2 = new Person(context) { FirstName = "Child2" };
        person.Children = [child0, child1, child2];

        // Act - Only update index 0 and 2 (skip index 1)
        person.ApplySubjectUpdate(new SubjectUpdate
        {
            Properties = new Dictionary<string, SubjectPropertyUpdate>
            {
                {
                    nameof(Person.Children),
                    SubjectPropertyUpdate.Create(
                        new SubjectPropertyCollectionUpdate
                        {
                            Index = 0,
                            Item = new SubjectUpdate
                            {
                                Properties = new Dictionary<string, SubjectPropertyUpdate>
                                {
                                    { nameof(Person.FirstName), SubjectPropertyUpdate.Create("Updated0") }
                                }
                            }
                        },
                        new SubjectPropertyCollectionUpdate
                        {
                            Index = 2,
                            Item = new SubjectUpdate
                            {
                                Properties = new Dictionary<string, SubjectPropertyUpdate>
                                {
                                    { nameof(Person.FirstName), SubjectPropertyUpdate.Create("Updated2") }
                                }
                            }
                        })
                }
            }
        }, DefaultSubjectFactory.Instance);

        // Assert - Index 1 unchanged, 0 and 2 updated
        Assert.Equal("Updated0", person.Children[0].FirstName);
        Assert.Equal("Child1", person.Children[1].FirstName); // Unchanged
        Assert.Equal("Updated2", person.Children[2].FirstName);
    }

    [Fact]
    public void WhenApplyingUpdateWithJsonElementValues_ThenValuesAreDeserialized()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var person = new Person(context);

        // Simulate JSON deserialization where values come as JsonElement
        var json = """{"firstName": "JsonPerson", "lastName": "FromElement"}""";
        var doc = JsonDocument.Parse(json);
        var firstNameElement = doc.RootElement.GetProperty("firstName");
        var lastNameElement = doc.RootElement.GetProperty("lastName");

        // Act
        person.ApplySubjectUpdate(new SubjectUpdate
        {
            Properties = new Dictionary<string, SubjectPropertyUpdate>
            {
                { nameof(Person.FirstName), SubjectPropertyUpdate.Create(firstNameElement) },
                { nameof(Person.LastName), SubjectPropertyUpdate.Create(lastNameElement) }
            }
        }, DefaultSubjectFactory.Instance);

        // Assert
        Assert.Equal("JsonPerson", person.FirstName);
        Assert.Equal("FromElement", person.LastName);
    }

    [Fact]
    public void WhenApplyingCollectionUpdateWithJsonElementIndex_ThenIndexIsConverted()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var person = new Person(context);

        // Simulate JSON deserialization where index comes as JsonElement
        var indexJson = "1";
        var indexElement = JsonDocument.Parse(indexJson).RootElement;

        // Act - Use Operations.Insert for creating new items
        person.ApplySubjectUpdate(new SubjectUpdate
        {
            Properties = new Dictionary<string, SubjectPropertyUpdate>
            {
                {
                    nameof(Person.Children),
                    new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Collection,
                        Operations =
                        [
                            new SubjectCollectionOperation
                            {
                                Action = SubjectCollectionOperationType.Insert,
                                Index = 0,
                                Item = new SubjectUpdate
                                {
                                    Properties = new Dictionary<string, SubjectPropertyUpdate>
                                    {
                                        { nameof(Person.FirstName), SubjectPropertyUpdate.Create("First") }
                                    }
                                }
                            },
                            new SubjectCollectionOperation
                            {
                                Action = SubjectCollectionOperationType.Insert,
                                Index = indexElement, // JsonElement index
                                Item = new SubjectUpdate
                                {
                                    Properties = new Dictionary<string, SubjectPropertyUpdate>
                                    {
                                        { nameof(Person.FirstName), SubjectPropertyUpdate.Create("Second") }
                                    }
                                }
                            }
                        ]
                    }
                }
            }
        }, DefaultSubjectFactory.Instance);

        // Assert
        Assert.Equal(2, person.Children.Count);
        Assert.Equal("First", person.Children[0].FirstName);
        Assert.Equal("Second", person.Children[1].FirstName);
    }

    [Fact]
    public void WhenApplyingFromSource_ThenReceivedTimestampIsSet()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();
        var person = new Person(context);
        var source = new object();

        var changedTimestamp = DateTimeOffset.UtcNow.AddMinutes(-5);
        var beforeApply = DateTimeOffset.UtcNow;

        SubjectPropertyChange? capturedChange = null;
        using var subscription = context
            .GetPropertyChangeObservable(System.Reactive.Concurrency.ImmediateScheduler.Instance)
            .Subscribe(c => capturedChange = c);

        // Act
        person.ApplySubjectUpdateFromSource(
            new SubjectUpdate
            {
                Properties = new Dictionary<string, SubjectPropertyUpdate>
                {
                    { nameof(Person.FirstName), SubjectPropertyUpdate.Create("John", changedTimestamp) }
                }
            },
            source,
            DefaultSubjectFactory.Instance);

        var afterApply = DateTimeOffset.UtcNow;

        // Assert - Received timestamp should be set to approximately now
        Assert.NotNull(capturedChange);
        var change = capturedChange.Value;
        Assert.NotNull(change.ReceivedTimestamp);
        Assert.True(change.ReceivedTimestamp >= beforeApply);
        Assert.True(change.ReceivedTimestamp <= afterApply);
        Assert.Equal(changedTimestamp, change.ChangedTimestamp);
    }

    [Fact]
    public void WhenApplyingCollectionGrows_ThenExistingItemsReusedAndNewItemsCreated()
    {
        // Arrange - Create person with 2 existing children
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var person = new Person(context);
        var existingChild0 = new Person(context) { FirstName = "Existing0" };
        var existingChild1 = new Person(context) { FirstName = "Existing1" };
        person.Children = [existingChild0, existingChild1];

        // Act - Apply update that:
        // 1. Inserts a new third child (structural change via Operations)
        // 2. Updates existing items' properties (sparse updates via Collection)
        person.ApplySubjectUpdate(new SubjectUpdate
        {
            Properties = new Dictionary<string, SubjectPropertyUpdate>
            {
                {
                    nameof(Person.Children),
                    new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Collection,
                        Operations =
                        [
                            new SubjectCollectionOperation
                            {
                                Action = SubjectCollectionOperationType.Insert,
                                Index = 2,
                                Item = new SubjectUpdate
                                {
                                    Properties = new Dictionary<string, SubjectPropertyUpdate>
                                    {
                                        { nameof(Person.FirstName), SubjectPropertyUpdate.Create("NewChild2") }
                                    }
                                }
                            }
                        ],
                        Collection =
                        [
                            new SubjectPropertyCollectionUpdate
                            {
                                Index = 0,
                                Item = new SubjectUpdate
                                {
                                    Properties = new Dictionary<string, SubjectPropertyUpdate>
                                    {
                                        { nameof(Person.FirstName), SubjectPropertyUpdate.Create("Updated0") }
                                    }
                                }
                            },
                            new SubjectPropertyCollectionUpdate
                            {
                                Index = 1,
                                Item = new SubjectUpdate
                                {
                                    Properties = new Dictionary<string, SubjectPropertyUpdate>
                                    {
                                        { nameof(Person.FirstName), SubjectPropertyUpdate.Create("Updated1") }
                                    }
                                }
                            }
                        ]
                    }
                }
            }
        }, DefaultSubjectFactory.Instance);

        // Assert
        Assert.Equal(3, person.Children.Count);

        // Existing items should be reused
        Assert.Same(existingChild0, person.Children[0]);
        Assert.Same(existingChild1, person.Children[1]);
        Assert.Equal("Updated0", person.Children[0].FirstName);
        Assert.Equal("Updated1", person.Children[1].FirstName);

        // New item should be created
        Assert.NotSame(existingChild0, person.Children[2]);
        Assert.NotSame(existingChild1, person.Children[2]);
        Assert.Equal("NewChild2", person.Children[2].FirstName);
    }

    [Fact]
    public void WhenApplyingMoveOperation_ThenIdentityIsPreserved()
    {
        // Arrange - Create person with 3 existing children
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var person = new Person(context);
        var child0 = new Person(context) { FirstName = "Child0" };
        var child1 = new Person(context) { FirstName = "Child1" };
        var child2 = new Person(context) { FirstName = "Child2" };
        person.Children = [child0, child1, child2];

        // Act - Move child2 to position 0 (reorder)
        person.ApplySubjectUpdate(new SubjectUpdate
        {
            Properties = new Dictionary<string, SubjectPropertyUpdate>
            {
                {
                    nameof(Person.Children),
                    new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Collection,
                        Operations =
                        [
                            new SubjectCollectionOperation
                            {
                                Action = SubjectCollectionOperationType.Move,
                                FromIndex = 2,
                                Index = 0
                            }
                        ]
                    }
                }
            }
        }, DefaultSubjectFactory.Instance);

        // Assert - Identity should be preserved, order should change
        Assert.Equal(3, person.Children.Count);
        Assert.Same(child2, person.Children[0]); // child2 moved to front
        Assert.Same(child0, person.Children[1]); // child0 shifted
        Assert.Same(child1, person.Children[2]); // child1 shifted
    }

    [Fact]
    public void WhenApplyingRemoveOperation_ThenItemIsRemoved()
    {
        // Arrange - Create person with 3 existing children
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var person = new Person(context);
        var child0 = new Person(context) { FirstName = "Child0" };
        var child1 = new Person(context) { FirstName = "Child1" };
        var child2 = new Person(context) { FirstName = "Child2" };
        person.Children = [child0, child1, child2];

        // Act - Remove middle child
        person.ApplySubjectUpdate(new SubjectUpdate
        {
            Properties = new Dictionary<string, SubjectPropertyUpdate>
            {
                {
                    nameof(Person.Children),
                    new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Collection,
                        Operations =
                        [
                            new SubjectCollectionOperation
                            {
                                Action = SubjectCollectionOperationType.Remove,
                                Index = 1
                            }
                        ]
                    }
                }
            }
        }, DefaultSubjectFactory.Instance);

        // Assert - Middle item removed, others preserved
        Assert.Equal(2, person.Children.Count);
        Assert.Same(child0, person.Children[0]);
        Assert.Same(child2, person.Children[1]);
    }

    [Fact]
    public void WhenApplyingMoveWithPropertyUpdate_ThenBothAreApplied()
    {
        // Arrange - Create person with 3 existing children
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var person = new Person(context);
        var child0 = new Person(context) { FirstName = "Child0" };
        var child1 = new Person(context) { FirstName = "Child1" };
        var child2 = new Person(context) { FirstName = "Child2" };
        person.Children = [child0, child1, child2];

        // Act - Move child2 to front AND update its property
        person.ApplySubjectUpdate(new SubjectUpdate
        {
            Properties = new Dictionary<string, SubjectPropertyUpdate>
            {
                {
                    nameof(Person.Children),
                    new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Collection,
                        Operations =
                        [
                            new SubjectCollectionOperation
                            {
                                Action = SubjectCollectionOperationType.Move,
                                FromIndex = 2,
                                Index = 0
                            }
                        ],
                        Collection =
                        [
                            new SubjectPropertyCollectionUpdate
                            {
                                Index = 0, // Final index after move
                                Item = new SubjectUpdate
                                {
                                    Properties = new Dictionary<string, SubjectPropertyUpdate>
                                    {
                                        { nameof(Person.FirstName), SubjectPropertyUpdate.Create("MovedAndUpdated") }
                                    }
                                }
                            }
                        ]
                    }
                }
            }
        }, DefaultSubjectFactory.Instance);

        // Assert - Move AND property update should both be applied
        Assert.Equal(3, person.Children.Count);
        Assert.Same(child2, person.Children[0]); // Same instance
        Assert.Equal("MovedAndUpdated", person.Children[0].FirstName); // Property updated
    }

    [Fact]
    public void WhenApplyingDictionaryInsert_ThenItemIsAdded()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var node = new CycleTestNode(context) { Name = "Root" };

        // Act - Insert new dictionary item
        node.ApplySubjectUpdate(new SubjectUpdate
        {
            Properties = new Dictionary<string, SubjectPropertyUpdate>
            {
                {
                    nameof(CycleTestNode.Lookup),
                    new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Collection,
                        Operations =
                        [
                            new SubjectCollectionOperation
                            {
                                Action = SubjectCollectionOperationType.Insert,
                                Index = "key1",
                                Item = new SubjectUpdate
                                {
                                    Properties = new Dictionary<string, SubjectPropertyUpdate>
                                    {
                                        { nameof(CycleTestNode.Name), SubjectPropertyUpdate.Create("Item1") }
                                    }
                                }
                            }
                        ]
                    }
                }
            }
        }, DefaultSubjectFactory.Instance);

        // Assert
        Assert.Single(node.Lookup);
        Assert.True(node.Lookup.ContainsKey("key1"));
        Assert.Equal("Item1", node.Lookup["key1"].Name);
    }

    [Fact]
    public void WhenApplyingDictionaryRemove_ThenItemIsRemoved()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var node = new CycleTestNode(context) { Name = "Root" };
        var item1 = new CycleTestNode(context) { Name = "Item1" };
        var item2 = new CycleTestNode(context) { Name = "Item2" };
        node.Lookup = new Dictionary<string, CycleTestNode> { ["key1"] = item1, ["key2"] = item2 };

        // Act - Remove key1
        node.ApplySubjectUpdate(new SubjectUpdate
        {
            Properties = new Dictionary<string, SubjectPropertyUpdate>
            {
                {
                    nameof(CycleTestNode.Lookup),
                    new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Collection,
                        Operations =
                        [
                            new SubjectCollectionOperation
                            {
                                Action = SubjectCollectionOperationType.Remove,
                                Index = "key1"
                            }
                        ]
                    }
                }
            }
        }, DefaultSubjectFactory.Instance);

        // Assert
        Assert.Single(node.Lookup);
        Assert.False(node.Lookup.ContainsKey("key1"));
        Assert.Same(item2, node.Lookup["key2"]);
    }

    [Fact]
    public void WhenApplyingDictionaryPropertyUpdate_ThenItemIsUpdated()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var node = new CycleTestNode(context) { Name = "Root" };
        var item1 = new CycleTestNode(context) { Name = "Item1" };
        node.Lookup = new Dictionary<string, CycleTestNode> { ["key1"] = item1 };

        // Act - Update property of existing item
        node.ApplySubjectUpdate(new SubjectUpdate
        {
            Properties = new Dictionary<string, SubjectPropertyUpdate>
            {
                {
                    nameof(CycleTestNode.Lookup),
                    new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Collection,
                        Collection =
                        [
                            new SubjectPropertyCollectionUpdate
                            {
                                Index = "key1",
                                Item = new SubjectUpdate
                                {
                                    Properties = new Dictionary<string, SubjectPropertyUpdate>
                                    {
                                        { nameof(CycleTestNode.Name), SubjectPropertyUpdate.Create("UpdatedItem1") }
                                    }
                                }
                            }
                        ]
                    }
                }
            }
        }, DefaultSubjectFactory.Instance);

        // Assert - Same instance, property updated
        Assert.Single(node.Lookup);
        Assert.Same(item1, node.Lookup["key1"]);
        Assert.Equal("UpdatedItem1", node.Lookup["key1"].Name);
    }
}