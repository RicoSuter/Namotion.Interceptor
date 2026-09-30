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
        // Arrange: the receiver constructs its own child, which takes the sender's ID with the first complete update
        var logger = new RecordingLogger();
        var sourceContext = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions().WithRegistry();
        var sourceChild = new InitOnlyTypesTestNode { Name = "Stale" };
        var source = collection
            ? new InitOnlyTypesTestNode(sourceContext) { Items = [sourceChild] }
            : new InitOnlyTypesTestNode(sourceContext) { Child = sourceChild };
        var targetContext = InterceptorSubjectContext.Create().WithRegistry()
            .WithService<ILoggerFactory>(() => new RecordingLoggerFactory(logger));
        var existingChild = new InitOnlyTypesTestNode { Name = "Stale" };
        var target = collection
            ? new InitOnlyTypesTestNode(targetContext) { Items = [existingChild] }
            : new InitOnlyTypesTestNode(targetContext) { Child = existingChild };
        target.ApplySubjectUpdate(SubjectUpdate.CreateCompleteUpdate(source, []), DefaultSubjectFactory.Instance, ChangeOrigin.Local);

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
        Assert.Equal(((IInterceptorSubject)sourceChild).TryGetSubjectId(), ((IInterceptorSubject)existingChild).TryGetSubjectId());
        Assert.Empty(logger.Warnings);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhenTheSenderRestartsWithNewIds_ThenTheHeldChildTakesTheNewIdAndKeepsReceivingChanges(bool collection)
    {
        // Arrange
        var targetContext = InterceptorSubjectContext.Create().WithRegistry();
        var existingChild = new InitOnlyTypesTestNode { Name = "Stale" };
        var target = collection
            ? new InitOnlyTypesTestNode(targetContext) { Items = [existingChild] }
            : new InitOnlyTypesTestNode(targetContext) { Child = existingChild };
        var (firstSource, _, _) = CreateInitOnlySource(collection, "First");
        target.ApplySubjectUpdate(SubjectUpdate.CreateCompleteUpdate(firstSource, []), DefaultSubjectFactory.Instance, ChangeOrigin.Local);
        var firstId = ((IInterceptorSubject)existingChild).TryGetSubjectId();

        // Act: a restarted sender assigns new IDs to an equal graph with new values
        var (restartedSource, restartedChild, restartedContext) = CreateInitOnlySource(collection, "Restarted");
        target.ApplySubjectUpdate(SubjectUpdate.CreateCompleteUpdate(restartedSource, []), DefaultSubjectFactory.Instance, ChangeOrigin.Local);
        var changes = new List<SubjectPropertyChange>();
        using (restartedContext.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(changes.Add))
        {
            restartedChild.Name = "Changed";
        }

        target.ApplySubjectUpdate(SubjectUpdate.CreatePartialUpdateFromChanges(restartedSource, changes.ToArray(), []),
            DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        var childId = ((IInterceptorSubject)restartedChild).TryGetSubjectId()!;
        Assert.NotEqual(firstId, childId);
        Assert.Same(existingChild, collection ? Assert.Single(target.Items) : target.Child);
        Assert.Equal(childId, ((IInterceptorSubject)existingChild).TryGetSubjectId());
        Assert.Equal("Changed", existingChild.Name);
        var idRegistry = targetContext.GetService<Namotion.Interceptor.Registry.Abstractions.ISubjectIdRegistry>();
        Assert.True(idRegistry.TryGetSubjectById(childId, out var registered));
        Assert.Same(existingChild, registered);
        Assert.False(idRegistry.TryGetSubjectById(firstId!, out _));
    }

    private static (InitOnlyTypesTestNode Source, InitOnlyTypesTestNode Child, IInterceptorSubjectContext Context) CreateInitOnlySource(
        bool collection, string name)
    {
        var context = InterceptorSubjectContext.Create().WithPropertyChangeSubscriptions().WithRegistry();
        var child = new InitOnlyTypesTestNode { Name = name };
        var source = collection
            ? new InitOnlyTypesTestNode(context) { Items = [child] }
            : new InitOnlyTypesTestNode(context) { Child = child };
        return (source, child, context);
    }

    [Fact]
    public void WhenACollectionIsInitOnly_ThenCompleteMembershipDoesNotRebuildIt()
    {
        // Arrange: membership the receiver cannot store, so no item is created for it and the container
        // keeps the one it already holds, which takes the ID listed at its position.
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
                        Items =
                        [
                            new SubjectPropertyItemUpdate { Id = "2" },
                            new SubjectPropertyItemUpdate { Id = "3" }
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
    public void WhenADictionaryIsInitOnly_ThenANewKeyIsDroppedWhileAnExistingChildIsStillUpdated()
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
        var update = new SubjectUpdate
        {
            Root = "1",
            Subjects = new Dictionary<string, Dictionary<string, SubjectPropertyUpdate>>
            {
                ["1"] = new()
                {
                    [nameof(InitOnlyTypesTestNode.Lookup)] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Dictionary,
                        Items =
                        [
                            new SubjectPropertyItemUpdate { Key = "existing", Id = "2" },
                            new SubjectPropertyItemUpdate { Key = "added", Id = "3" }
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
    public void WhenANullReferenceUpdateNamesAReferenceWithoutASetter_ThenTheHeldSubjectStaysWithoutFailureOrWarning()
    {
        // Arrange
        var logger = new RecordingLogger();
        var existingChild = new InitOnlyTypesTestNode { Name = "Existing" };
        var target = new InitOnlyTypesTestNode(InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithService<ILoggerFactory>(() => new RecordingLoggerFactory(logger)))
        {
            Child = existingChild
        };
        var update = new SubjectUpdate
        {
            Root = "1",
            Subjects = new Dictionary<string, Dictionary<string, SubjectPropertyUpdate>>
            {
                ["1"] = new()
                {
                    [nameof(InitOnlyTypesTestNode.Child)] = new SubjectPropertyUpdate { Kind = SubjectPropertyUpdateKind.Object }
                }
            }
        };

        // Act
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Assert.Same(existingChild, target.Child);
        Assert.Empty(logger.Warnings);
    }

    [Fact]
    public void WhenAReferenceWithoutASetterHoldsASubjectWithAnIdOfItsOwn_ThenItTakesTheNamedIdAndPayload()
    {
        // Arrange: the receiver gave the held child an ID before its first update, as a relay does that creates
        // an update of its own graph first
        var logger = new RecordingLogger();
        var existingChild = new InitOnlyTypesTestNode { Name = "Existing" };
        var targetContext = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithService<ILoggerFactory>(() => new RecordingLoggerFactory(logger));
        var target = new InitOnlyTypesTestNode(targetContext) { Child = existingChild };
        existingChild.SetSubjectId("held");

        // Act
        target.ApplySubjectUpdate(CreateHeldChildUpdate(), DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Assert.Same(existingChild, target.Child);
        Assert.Equal("Replacement", existingChild.Name);
        Assert.Equal("2", ((IInterceptorSubject)existingChild).TryGetSubjectId());
        var idRegistry = targetContext.GetService<Namotion.Interceptor.Registry.Abstractions.ISubjectIdRegistry>();
        Assert.False(idRegistry.TryGetSubjectById("held", out _));
        Assert.Empty(logger.Warnings);
    }

    [Fact]
    public void WhenAReferenceWithoutASetterNamesASubjectHeldElsewhere_ThenTheHeldSubjectKeepsItsIdAndValuesAndTheStructureIsReportedDropped()
    {
        // Arrange: the ID the update names already resolves to another subject of this graph
        var logger = new RecordingLogger();
        var existingChild = new InitOnlyTypesTestNode { Name = "Existing" };
        var otherChild = new InitOnlyTypesTestNode { Name = "Other" };
        var target = new InitOnlyTypesTestNode(InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithService<ILoggerFactory>(() => new RecordingLoggerFactory(logger)))
        {
            Child = existingChild,
            Items = [otherChild]
        };
        existingChild.SetSubjectId("held");
        otherChild.SetSubjectId("2");

        // Act
        target.ApplySubjectUpdate(CreateHeldChildUpdate(), DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Assert.Same(existingChild, target.Child);
        Assert.Equal("Existing", existingChild.Name);
        Assert.Equal("held", ((IInterceptorSubject)existingChild).TryGetSubjectId());
        Assert.Equal("Replacement", otherChild.Name);
        Assert.Contains($"{nameof(InitOnlyTypesTestNode)}.{nameof(InitOnlyTypesTestNode.Child)}", Assert.Single(logger.Warnings));
    }

    [Fact]
    public void WhenTheUpdateAlreadyAppliedTheHeldSubjectUnderItsCurrentId_ThenItKeepsThatId()
    {
        // Arrange: the entry of the held child's current ID is applied before the position naming another ID
        var logger = new RecordingLogger();
        var heldChild = new InitOnlyTypesTestNode { Name = "Existing" };
        var node = new InitOnlyTypesTestNode { Child = heldChild };
        var target = new InitOnlyTypesTestHolder(InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithService<ILoggerFactory>(() => new RecordingLoggerFactory(logger)))
        {
            Node = node
        };
        heldChild.SetSubjectId("held");
        node.SetSubjectId("node");
        var update = new SubjectUpdate
        {
            Root = "1",
            Subjects = new Dictionary<string, Dictionary<string, SubjectPropertyUpdate>>
            {
                ["held"] = new() { [nameof(InitOnlyTypesTestNode.Name)] = new SubjectPropertyUpdate { Kind = SubjectPropertyUpdateKind.Value, Value = "Held" } },
                ["node"] = new() { [nameof(InitOnlyTypesTestNode.Child)] = new SubjectPropertyUpdate { Kind = SubjectPropertyUpdateKind.Object, Id = "2" } },
                ["2"] = new() { [nameof(InitOnlyTypesTestNode.Name)] = new SubjectPropertyUpdate { Kind = SubjectPropertyUpdateKind.Value, Value = "Replacement" } }
            }
        };

        // Act
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Assert.Same(heldChild, node.Child);
        Assert.Equal("held", ((IInterceptorSubject)heldChild).TryGetSubjectId());
        Assert.Equal("Held", heldChild.Name);
        Assert.Contains($"{nameof(InitOnlyTypesTestNode)}.{nameof(InitOnlyTypesTestNode.Child)}", Assert.Single(logger.Warnings));
    }

    [Fact]
    public void WhenOneHeldSubjectIsNamedByTwoIds_ThenItKeepsTheIdItAdoptedFirst()
    {
        // Arrange: the same child is held at two positions the model cannot write, which name different IDs
        var heldChild = new InitOnlyTypesTestNode { Name = "Existing" };
        var target = new InitOnlyTypesTestNode(InterceptorSubjectContext.Create().WithRegistry())
        {
            Child = heldChild,
            Items = [heldChild]
        };
        var update = new SubjectUpdate
        {
            Root = "1",
            Subjects = new Dictionary<string, Dictionary<string, SubjectPropertyUpdate>>
            {
                ["1"] = new()
                {
                    [nameof(InitOnlyTypesTestNode.Child)] = new SubjectPropertyUpdate { Kind = SubjectPropertyUpdateKind.Object, Id = "first" },
                    [nameof(InitOnlyTypesTestNode.Items)] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Collection,
                        Items = [new SubjectPropertyItemUpdate { Id = "second" }]
                    }
                },
                ["second"] = new() { [nameof(InitOnlyTypesTestNode.Name)] = new SubjectPropertyUpdate { Kind = SubjectPropertyUpdateKind.Value, Value = "Second" } }
            }
        };

        // Act
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Assert.Equal("first", ((IInterceptorSubject)heldChild).TryGetSubjectId());
        Assert.Equal("Existing", heldChild.Name);
    }

    [Fact]
    public void WhenAReferenceWithoutASetterHoldsTheRootAndNamesAnotherId_ThenTheRootKeepsItsId()
    {
        // Arrange
        var root = new InitOnlyOwnerNode(InterceptorSubjectContext.Create().WithRegistry()) { Name = "Root" };
        var child = new InitOnlyOwnerNode { Name = "Child", Owner = root };
        root.Child = child;
        var rootId = ((IInterceptorSubject)root).GetOrAddSubjectId();
        child.SetSubjectId("child");
        var update = new SubjectUpdate
        {
            Root = "sender-root",
            Subjects = new Dictionary<string, Dictionary<string, SubjectPropertyUpdate>>
            {
                ["child"] = new() { [nameof(InitOnlyOwnerNode.Owner)] = new SubjectPropertyUpdate { Kind = SubjectPropertyUpdateKind.Object, Id = "other" } },
                ["other"] = new() { [nameof(InitOnlyOwnerNode.Name)] = new SubjectPropertyUpdate { Kind = SubjectPropertyUpdateKind.Value, Value = "Other" } }
            }
        };

        // Act
        root.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Assert.Same(root, child.Owner);
        Assert.Equal(rootId, ((IInterceptorSubject)root).TryGetSubjectId());
        Assert.Equal("Root", root.Name);
    }

    private static SubjectUpdate CreateHeldChildUpdate() => new()
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
                }
            },
            ["2"] = new()
            {
                [nameof(InitOnlyTypesTestNode.Name)] = new SubjectPropertyUpdate
                {
                    Kind = SubjectPropertyUpdateKind.Value,
                    Value = "Replacement"
                }
            }
        }
    };

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
                        Items = [new SubjectPropertyItemUpdate { Id = "3" }]
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
        // held items, so one qualified property name is dropped three times.
        var logger = new RecordingLogger();
        var node = new InitOnlyTypesTestNode { Items = [new InitOnlyTypesTestNode(), new InitOnlyTypesTestNode()] };
        var target = new InitOnlyTypesTestHolder(InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithService<ILoggerFactory>(() => new RecordingLoggerFactory(logger)))
        {
            Node = node
        };
        node.SetSubjectId("2");
        var update = new SubjectUpdate
        {
            Root = "1",
            Subjects = new Dictionary<string, Dictionary<string, SubjectPropertyUpdate>>
            {
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
                            new SubjectPropertyItemUpdate { Id = "4" },
                            new SubjectPropertyItemUpdate { Id = "5" }
                        ]
                    },
                    [nameof(InitOnlyTypesTestNode.Lookup)] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Dictionary,
                        Items = [new SubjectPropertyItemUpdate { Key = "added", Id = "6" }]
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
        Assert.Same(node, target.Node);
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

/// <summary>
/// Refers to the subject owning it through a reference it cannot write, so a held position can hold the root.
/// </summary>
[InterceptorSubject]
public partial class InitOnlyOwnerNode
{
    public partial string? Name { get; set; }

    public partial InitOnlyOwnerNode? Owner { get; init; }

    public partial InitOnlyOwnerNode? Child { get; set; }
}
