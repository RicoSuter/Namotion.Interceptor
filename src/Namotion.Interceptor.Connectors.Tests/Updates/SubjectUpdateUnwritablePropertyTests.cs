using System.Reactive.Concurrency;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Attributes;
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
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void WhenAReferenceOrCollectionIsInitOnly_ThenANestedChangeStillReachesTheExistingChild(bool collection, bool partial)
    {
        // Arrange
        var sourceContext = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions().WithRegistry();
        var sourceChild = new InitOnlyTypesTestNode { Name = "Stale" };
        var source = collection
            ? new InitOnlyTypesTestNode(sourceContext) { Items = [sourceChild] }
            : new InitOnlyTypesTestNode(sourceContext) { Child = sourceChild };
        var targetContext = InterceptorSubjectContext.Create().WithRegistry();
        var existingChild = new InitOnlyTypesTestNode { Name = "Stale" };
        var target = collection
            ? new InitOnlyTypesTestNode(targetContext) { Items = [existingChild] }
            : new InitOnlyTypesTestNode(targetContext) { Child = existingChild };

        var changes = new List<SubjectPropertyChange>();
        sourceContext.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(changes.Add);
        sourceChild.Name = "Alice";

        // Act
        var update = partial
            ? SubjectUpdate.CreatePartialUpdateFromChanges(source, changes.ToArray(), [])
            : SubjectUpdate.CreateCompleteUpdate(source, []);
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Assert.Same(existingChild, collection ? Assert.Single(target.Items) : target.Child);
        Assert.Equal("Alice", existingChild.Name);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhenADictionaryIsInitOnly_ThenANewKeyIsDroppedWhileAnExistingChildIsStillUpdated(bool completeMembership)
    {
        // Arrange
        var logger = new RecordingLogger();
        var existingChild = new InitOnlyTypesTestNode { Name = "Stale" };
        var target = new InitOnlyTypesTestNode(InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithService<ILoggerFactory>(() => new RecordingLoggerFactory(logger)))
        {
            Lookup = new() { ["existing"] = existingChild }
        };
        var lookupUpdate = completeMembership
            ? new SubjectPropertyUpdate
            {
                Kind = SubjectPropertyUpdateKind.Dictionary,
                Count = 2,
                Items =
                [
                    new SubjectPropertyItemUpdate { Index = "existing", Id = "2" },
                    new SubjectPropertyItemUpdate { Index = "added", Id = "3" }
                ]
            }
            : new SubjectPropertyUpdate
            {
                Kind = SubjectPropertyUpdateKind.Dictionary,
                Operations =
                [
                    new SubjectCollectionOperation
                    {
                        Action = SubjectCollectionOperationType.Insert,
                        Index = "added",
                        Id = "3"
                    }
                ],
                Items = [new SubjectPropertyItemUpdate { Index = "existing", Id = "2" }]
            };
        var update = new SubjectUpdate
        {
            Root = "1",
            Subjects = new Dictionary<string, Dictionary<string, SubjectPropertyUpdate>>
            {
                ["1"] = new() { [nameof(InitOnlyTypesTestNode.Lookup)] = lookupUpdate },
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
        var entry = Assert.Single(target.Lookup);
        Assert.Equal("existing", entry.Key);
        Assert.Same(existingChild, entry.Value);
        Assert.Equal("Updated", existingChild.Name);
        Assert.Contains($"{nameof(InitOnlyTypesTestNode)}.{nameof(InitOnlyTypesTestNode.Lookup)}", Assert.Single(logger.Warnings));
    }

    [Fact]
    public void WhenAValueUpdateNamesAPropertyWithoutASetter_ThenItIsIgnoredWithoutFailureOrWarning()
    {
        // Arrange: the payload does not even convert to the property type, so a value this model cannot
        // store must be dropped before the conversion. A producer legitimately publishes values the
        // receiving model computes itself, so warning here would fire on nearly every message.
        var logger = new RecordingLogger();
        var target = new InitOnlyTypesTestNode(InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithService<ILoggerFactory>(() => new RecordingLoggerFactory(logger)))
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
        Assert.Empty(logger.Warnings);
        Assert.Empty(logger.Errors);
    }

    [Fact]
    public void WhenAStructuralPayloadNamesAPropertyWithoutASetter_ThenOneWarningNamesEveryDroppedProperty()
    {
        // Arrange
        var logger = new RecordingLogger();
        var target = new InitOnlyTypesTestNode(InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithService<ILoggerFactory>(() => new RecordingLoggerFactory(logger)));
        var update = new SubjectUpdate
        {
            Root = "1",
            Subjects = new Dictionary<string, Dictionary<string, SubjectPropertyUpdate>>
            {
                ["1"] = new()
                {
                    [nameof(InitOnlyTypesTestNode.Child)] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Object,
                        Id = "2"
                    },
                    [nameof(InitOnlyTypesTestNode.Items)] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Collection,
                        Operations =
                        [
                            new SubjectCollectionOperation
                            {
                                Action = SubjectCollectionOperationType.Insert,
                                Index = 0,
                                Id = "3"
                            }
                        ]
                    },
                    [nameof(InitOnlyTypesTestNode.Label)] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Value,
                        Value = "Dropped"
                    }
                },
                ["2"] = new() { [nameof(InitOnlyTypesTestNode.Name)] = new SubjectPropertyUpdate { Value = "New child" } },
                ["3"] = new() { [nameof(InitOnlyTypesTestNode.Name)] = new SubjectPropertyUpdate { Value = "New item" } }
            }
        };

        // Act
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        var warning = Assert.Single(logger.Warnings);
        Assert.Contains(nameof(InitOnlyTypesTestNode.Child), warning);
        Assert.Contains(nameof(InitOnlyTypesTestNode.Items), warning);
        Assert.DoesNotContain(nameof(InitOnlyTypesTestNode.Label), warning);
        Assert.Null(target.Child);
        Assert.Empty(target.Items);
    }

    [Fact]
    public void WhenNestedSubjectsDropStructure_ThenTheWarningNamesEachDroppedPropertyOnceByItsOwnerType()
    {
        // Arrange: the node below a root of another type drops its own Child, and so does each of its two
        // items, so one qualified property name is dropped three times.
        var logger = new RecordingLogger();
        var node = new InitOnlyTypesTestNode { Items = [new InitOnlyTypesTestNode(), new InitOnlyTypesTestNode()] };
        var target = new InitOnlyTypesTestHolder(InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithService<ILoggerFactory>(() => new RecordingLoggerFactory(logger)))
        {
            Node = node
        };
        var update = new SubjectUpdate
        {
            Root = "1",
            Subjects = new Dictionary<string, Dictionary<string, SubjectPropertyUpdate>>
            {
                ["1"] = new()
                {
                    [nameof(InitOnlyTypesTestHolder.Node)] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Object,
                        Id = "2"
                    }
                },
                ["2"] = new()
                {
                    [nameof(InitOnlyTypesTestNode.Child)] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Object,
                        Id = "3"
                    },
                    [nameof(InitOnlyTypesTestNode.Items)] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Collection,
                        Items =
                        [
                            new SubjectPropertyItemUpdate { Index = 0, Id = "4" },
                            new SubjectPropertyItemUpdate { Index = 1, Id = "5" }
                        ]
                    },
                    [nameof(InitOnlyTypesTestNode.Lookup)] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Dictionary,
                        Operations =
                        [
                            new SubjectCollectionOperation
                            {
                                Action = SubjectCollectionOperationType.Insert,
                                Index = "added",
                                Id = "6"
                            }
                        ]
                    }
                },
                ["3"] = new(),
                ["4"] = new() { [nameof(InitOnlyTypesTestNode.Child)] = new SubjectPropertyUpdate { Kind = SubjectPropertyUpdateKind.Object, Id = "7" } },
                ["5"] = new() { [nameof(InitOnlyTypesTestNode.Child)] = new SubjectPropertyUpdate { Kind = SubjectPropertyUpdateKind.Object, Id = "8" } },
                ["6"] = new(),
                ["7"] = new(),
                ["8"] = new()
            }
        };

        // Act
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        var warning = Assert.Single(logger.Warnings);
        var droppedChild = $"{nameof(InitOnlyTypesTestNode)}.{nameof(InitOnlyTypesTestNode.Child)}";
        Assert.Single(Regex.Matches(warning, Regex.Escape(droppedChild)));
        Assert.Contains($"{nameof(InitOnlyTypesTestNode)}.{nameof(InitOnlyTypesTestNode.Lookup)}", warning);
        Assert.DoesNotContain($"{nameof(InitOnlyTypesTestHolder)}.{nameof(InitOnlyTypesTestNode.Child)}", warning);
        Assert.Null(node.Child);
        Assert.All(node.Items, item => Assert.Null(item.Child));
        Assert.Empty(node.Lookup);
    }
}

/// <summary>
/// Holds an <see cref="InitOnlyTypesTestNode"/> through a writable reference, so a property that node
/// cannot write belongs to a type other than the root the update is applied to.
/// </summary>
[InterceptorSubject]
public partial class InitOnlyTypesTestHolder
{
    public partial InitOnlyTypesTestNode? Node { get; set; }
}
