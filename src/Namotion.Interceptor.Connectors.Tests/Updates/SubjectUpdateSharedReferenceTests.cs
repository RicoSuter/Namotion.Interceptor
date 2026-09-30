using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.Connectors.Updates.Internal;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Tests.Updates;

/// <summary>
/// Pins reference identity for a subject ID that one update mentions more than once. The applier
/// resolves references through the ID registry, which does not know a subject the same apply created
/// until its subtree is rooted, and does not know the sender's root ID at all. Every case below would
/// otherwise fabricate a second instance for the ID: the second one never gets populated, because the
/// first one consumed the ID's property entries, and it stays invisible to every later update because
/// the registry keeps the first one in its reverse index.
/// </summary>
[Collection(SubjectUpdateDiagnosticsCollection.Name)]
public class SubjectUpdateSharedReferenceTests
{
    [Fact]
    public void WhenTwoNewSubjectsReferenceTheSameNewSubject_ThenBothReferencesResolveToOneInstance()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var target = new CycleTestNode(context) { Name = "Root" };
        var rootId = target.GetOrAddSubjectId();

        var update = new SubjectUpdate
        {
            Root = rootId,
            Subjects = new()
            {
                [rootId] = new()
                {
                    ["Items"] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Collection,
                        Items =
                        [
                            new SubjectPropertyItemUpdate { Id = "first" },
                            new SubjectPropertyItemUpdate { Id = "second" }
                        ]
                    }
                },
                ["first"] = new()
                {
                    ["Name"] = CreateValueUpdate("First"),
                    ["Child"] = CreateObjectUpdate("shared")
                },
                ["second"] = new()
                {
                    ["Name"] = CreateValueUpdate("Second"),
                    ["Child"] = CreateObjectUpdate("shared")
                },
                ["shared"] = new()
                {
                    ["Name"] = CreateValueUpdate("Shared")
                }
            }
        };

        // Act
        SubjectUpdateApplier.ApplyUpdate(target, update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Assert.Equal(2, target.Items.Count);
        Assert.NotNull(target.Items[0].Child);
        Assert.NotNull(target.Items[1].Child);
        Assert.Same(target.Items[0].Child, target.Items[1].Child);
        Assert.Equal("Shared", target.Items[1].Child!.Name);
    }

    [Fact]
    public void WhenItemsArrayContainsTheSameIdTwice_ThenBothEntriesResolveToOneInstance()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var target = new CycleTestNode(context) { Name = "Root" };
        var rootId = target.GetOrAddSubjectId();

        var update = new SubjectUpdate
        {
            Root = rootId,
            Subjects = new()
            {
                [rootId] = new()
                {
                    ["Items"] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Collection,
                        Items =
                        [
                            new SubjectPropertyItemUpdate { Id = "repeated" },
                            new SubjectPropertyItemUpdate { Id = "repeated" }
                        ]
                    }
                },
                ["repeated"] = new()
                {
                    ["Name"] = CreateValueUpdate("Repeated")
                }
            }
        };

        // Act
        SubjectUpdateApplier.ApplyUpdate(target, update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Assert.Equal(2, target.Items.Count);
        Assert.Same(target.Items[0], target.Items[1]);
        Assert.Equal("Repeated", target.Items[1].Name);
    }

    [Fact]
    public void WhenNewSubjectReferencesItself_ThenTheReferenceResolvesToItself()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var target = new CycleTestNode(context) { Name = "Root" };
        var rootId = target.GetOrAddSubjectId();

        var update = new SubjectUpdate
        {
            Root = rootId,
            Subjects = new()
            {
                [rootId] = new()
                {
                    ["Child"] = CreateObjectUpdate("selfReferencing")
                },
                ["selfReferencing"] = new()
                {
                    ["Name"] = CreateValueUpdate("SelfReferencing"),
                    ["Self"] = CreateObjectUpdate("selfReferencing")
                }
            }
        };

        // Act
        SubjectUpdateApplier.ApplyUpdate(target, update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Assert.NotNull(target.Child);
        Assert.Same(target.Child, target.Child!.Self);
        Assert.Equal("SelfReferencing", target.Child.Self!.Name);
    }

    [Fact]
    public void WhenTwoNewSubjectsFormACycle_ThenTheReferencesResolveToEachOther()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var target = new CycleTestNode(context) { Name = "Root" };
        var rootId = target.GetOrAddSubjectId();

        var update = new SubjectUpdate
        {
            Root = rootId,
            Subjects = new()
            {
                [rootId] = new()
                {
                    ["Child"] = CreateObjectUpdate("upper")
                },
                ["upper"] = new()
                {
                    ["Name"] = CreateValueUpdate("Upper"),
                    ["Child"] = CreateObjectUpdate("lower")
                },
                ["lower"] = new()
                {
                    ["Name"] = CreateValueUpdate("Lower"),
                    ["Parent"] = CreateObjectUpdate("upper")
                }
            }
        };

        // Act
        SubjectUpdateApplier.ApplyUpdate(target, update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        var upper = target.Child;
        Assert.NotNull(upper);
        Assert.NotNull(upper!.Child);
        Assert.Same(upper, upper.Child!.Parent);
        Assert.Equal("Upper", upper.Child.Parent!.Name);
    }

    [Fact]
    public void WhenNewSubjectReferencesTheUpdateRoot_ThenTheReferenceResolvesToTheLocalRoot()
    {
        // Arrange: the sender's root ID is not the receiver's, which is what makes a reference back
        // to the root unresolvable through the receiver's registry.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var target = new CycleTestNode(context) { Name = "Root" };
        const string senderRootId = "senderRoot";

        var update = new SubjectUpdate
        {
            Root = senderRootId,
            Subjects = new()
            {
                [senderRootId] = new()
                {
                    ["Name"] = CreateValueUpdate("Root"),
                    ["Child"] = CreateObjectUpdate("child")
                },
                ["child"] = new()
                {
                    ["Name"] = CreateValueUpdate("Child"),
                    ["Parent"] = CreateObjectUpdate(senderRootId)
                }
            }
        };

        // Act
        SubjectUpdateApplier.ApplyUpdate(target, update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Assert.NotNull(target.Child);
        Assert.Same(target, target.Child!.Parent);
        Assert.False(
            context.GetService<ISubjectIdRegistry>().TryGetSubjectById(senderRootId, out _),
            "The sender's root ID must not end up in the receiver's reverse index, which is what "
            + "happens when the reference fabricates a phantom root and that phantom is rooted.");
    }

    [Fact]
    public void WhenPartialUpdateReferencesTheUpdateRoot_ThenTheReferenceAppliesWithoutDrop()
    {
        // Arrange: a partial update marks nothing complete, so a reference the receiver cannot
        // resolve is dropped rather than created. The sender never resends it, so the drop is
        // permanent divergence for a parent-pointer model.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var child = new CycleTestNode { Name = "Child" };
        var target = new CycleTestNode(context) { Name = "Root", Child = child };
        var childId = child.GetOrAddSubjectId();
        const string senderRootId = "senderRoot";

        var update = new SubjectUpdate
        {
            Root = senderRootId,
            CompleteSubjectIds = [],
            Subjects = new()
            {
                [childId] = new()
                {
                    ["Parent"] = CreateObjectUpdate(senderRootId)
                }
            }
        };

        var droppedBefore = SubjectUpdateDiagnostics.DroppedInboundSubjectUpdates;

        // Act
        SubjectUpdateApplier.ApplyUpdate(target, update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Assert.Same(target, child.Parent);
        Assert.Equal(droppedBefore, SubjectUpdateDiagnostics.DroppedInboundSubjectUpdates);
    }

    [Fact]
    public void WhenPartialUpdateRootHasNoOwnProperties_ThenTheUpdateStillCarriesTheRoot()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var child = new CycleTestNode { Name = "Child" };
        var root = new CycleTestNode(context) { Name = "Root", Child = child };

        var changes = new List<SubjectPropertyChange>();
        using (context
            .GetPropertyChangeObservable(System.Reactive.Concurrency.ImmediateScheduler.Instance)
            .Subscribe(change => changes.Add(change)))
        {
            child.Name = "Updated";
        }

        // Act
        var update = SubjectUpdate.CreatePartialUpdateFromChanges(root, changes.ToArray(), []);

        // Assert: the root carries no property entry of its own here, and the update still names it,
        // because that mapping is the receiver's only way to resolve a reference to the sender's root.
        Assert.NotNull(update.Root);
        Assert.Equal(root.GetOrAddSubjectId(), update.Root);
        Assert.False(update.Subjects.ContainsKey(update.Root!));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void WhenAnUpdateNamesOneSubjectFromReferenceAndCollectionPositions_ThenEveryPositionHoldsOneInstance(bool collectionFirst, bool heldBefore)
    {
        // Arrange: a position that already holds an instance of its own gets the named subject as well
        var target = new Person(InterceptorSubjectContext.Create().WithRegistry());
        if (heldBefore)
        {
            target.Children = [new Person { FirstName = "Stale" }];
        }

        var collection = new SubjectPropertyUpdate
        {
            Kind = SubjectPropertyUpdateKind.Collection,
            Items = [new SubjectPropertyItemUpdate { Id = "m" }]
        };
        var update = new SubjectUpdate
        {
            Root = "r",
            Subjects = new Dictionary<string, Dictionary<string, SubjectPropertyUpdate>>
            {
                ["r"] = collectionFirst
                    ? new() { [nameof(Person.Children)] = collection, [nameof(Person.Mother)] = CreateObjectUpdate("m"), [nameof(Person.Father)] = CreateObjectUpdate("m") }
                    : new() { [nameof(Person.Mother)] = CreateObjectUpdate("m"), [nameof(Person.Children)] = collection, [nameof(Person.Father)] = CreateObjectUpdate("m") },
                ["m"] = new() { [nameof(Person.FirstName)] = CreateValueUpdate("Eve") }
            }
        };

        // Act
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Assert.Equal("Eve", target.Mother!.FirstName);
        Assert.Same(target.Mother, Assert.Single(target.Children));
        Assert.Same(target.Mother, target.Father);
    }

    [Fact]
    public void WhenAReferenceNamesTheUpdateRootAgain_ThenTheApplyTerminatesWithTheRootReferencingItself()
    {
        // Arrange: the root's own payload is reached again through its child position
        var nested = new CycleTestNode { Name = "Stale" };
        nested.Child = nested;
        var target = new CycleTestNode(InterceptorSubjectContext.Create().WithRegistry()) { Name = "Stale", Child = nested };
        var update = new SubjectUpdate
        {
            Root = "r",
            Subjects = new Dictionary<string, Dictionary<string, SubjectPropertyUpdate>>
            {
                ["r"] = new()
                {
                    [nameof(CycleTestNode.Name)] = CreateValueUpdate("Named"),
                    [nameof(CycleTestNode.Child)] = CreateObjectUpdate("r")
                }
            }
        };

        // Act
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Assert.Equal("Named", target.Name);
        Assert.Same(target, target.Child);
        Assert.Equal("Stale", nested.Name);
    }

    [Theory]
    [InlineData("reference")]
    [InlineData("list")]
    [InlineData("dictionary")]
    public void WhenAMirrorSharesOneInstanceBetweenTwoPositionsTheSourceHasSplit_ThenACompleteUpdateGivesEachPositionItsOwnSubject(string position)
    {
        // Arrange
        var shared = new Person { FirstName = "Stale" };
        var first = new Person { FirstName = "First" };
        var second = new Person { FirstName = "Second" };
        var source = new Person(InterceptorSubjectContext.Create().WithRegistry());
        var mirror = new Person(InterceptorSubjectContext.Create().WithRegistry());
        switch (position)
        {
            case "reference":
                (source.Father, source.Mother) = (first, second);
                (mirror.Father, mirror.Mother) = (shared, shared);
                break;
            case "list":
                source.Children = [first, second];
                mirror.Children = [shared, shared];
                break;
            default:
                source.Relationships = new() { ["first"] = first, ["second"] = second };
                mirror.Relationships = new() { ["first"] = shared, ["second"] = shared };
                break;
        }

        // Act
        mirror.ApplySubjectUpdate(SubjectUpdate.CreateCompleteUpdate(source, []), DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Person[] positions = position switch
        {
            "reference" => [mirror.Father!, mirror.Mother!],
            "list" => [.. mirror.Children],
            _ => [mirror.Relationships!["first"], mirror.Relationships["second"]]
        };
        Assert.NotSame(positions[0], positions[1]);
        Assert.Equal(["First", "Second"], positions.Select(person => person.FirstName));
    }

    [Fact]
    public void WhenOneOfTwoReferencesToASharedSubjectIsReassigned_ThenTheMirrorKeepsTheSharedSubjectInTheOther()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var shared = new Person { FirstName = "Shared" };
        var source = new Person(context) { Father = shared, Mother = shared };
        var mirror = CreateMirror(source);
        var mirroredShared = mirror.Father;
        var changes = CaptureChanges(context, () => source.Mother = new Person { FirstName = "New" });

        // Act
        mirror.ApplySubjectUpdate(SubjectUpdate.CreatePartialUpdateFromChanges(source, changes, []), DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Assert.Same(mirroredShared, mirror.Father);
        Assert.Equal("Shared", mirror.Father!.FirstName);
        Assert.Equal("New", mirror.Mother!.FirstName);
    }

    [Fact]
    public void WhenAReferenceSharingAListChildIsReassigned_ThenTheListChildIsLeftIntact()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var shared = new Person { FirstName = "Shared" };
        var source = new Person(context) { Father = shared, Children = [shared] };
        var mirror = CreateMirror(source);
        var mirroredChild = Assert.Single(mirror.Children);
        var changes = CaptureChanges(context, () => source.Father = new Person { FirstName = "New" });

        // Act
        mirror.ApplySubjectUpdate(SubjectUpdate.CreatePartialUpdateFromChanges(source, changes, []), DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Assert.Same(mirroredChild, Assert.Single(mirror.Children));
        Assert.Equal("Shared", mirroredChild.FirstName);
        Assert.Equal("New", mirror.Father!.FirstName);
    }

    [Fact]
    public void WhenADictionaryIsRebuiltWithTheSameEntriesWhileAnotherSubjectMovesIn_ThenTheMirrorLosesNoMember()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var first = new Person { FirstName = "First" };
        var second = new Person { FirstName = "Second" };
        var moved = new Person { FirstName = "Moved" };
        var source = new Person(context)
        {
            Father = moved,
            Relationships = new() { ["first"] = first, ["second"] = second }
        };
        var mirror = CreateMirror(source);
        var mirroredSecond = mirror.Relationships!["second"];

        // The batch rebuilds the dictionary with the same entries and changes two subjects; the move of one
        // of them into the dictionary lands after the batch was captured.
        var changes = CaptureChanges(context, () =>
        {
            source.Relationships = new() { ["first"] = first, ["second"] = second };
            first.FirstName = "First changed";
            moved.FirstName = "Moved changed";
        });
        source.Father = null;
        source.Relationships = new() { ["first"] = first, ["second"] = second, ["moved"] = moved };

        // Act
        mirror.ApplySubjectUpdate(SubjectUpdate.CreatePartialUpdateFromChanges(source, changes, []), DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Assert.Same(mirroredSecond, mirror.Relationships!["second"]);
        Assert.Equal("First changed", mirror.Relationships["first"].FirstName);
    }

    [Fact]
    public void WhenABackReferenceIsReassignedToAnAncestor_ThenThePreviousTargetIsLeftIntact()
    {
        // Arrange: assigning the ancestor completes it, which states the changed subject's reference again
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var previous = new CycleTestNode { Name = "Previous" };
        var child = new CycleTestNode { Name = "Child" };
        var ancestor = new CycleTestNode { Name = "Ancestor" };
        var source = new CycleTestNode(context) { Name = "Root", Items = [previous], Child = ancestor };
        ancestor.Child = child;
        child.Parent = previous;
        var mirror = CreateMirror(source);
        var changes = CaptureChanges(context, () => child.Parent = ancestor);

        // Act
        mirror.ApplySubjectUpdate(SubjectUpdate.CreatePartialUpdateFromChanges(source, changes, []), DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Assert.Equal("Previous", mirror.Items[0].Name);
        Assert.Same(mirror.Child, mirror.Child!.Child!.Parent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhenAReferenceSharedWithAListItemIsReassignedWhileItsSubjectIsCompleted_ThenTheListItemIsLeftIntact(bool reassignmentFirst)
    {
        // Arrange: attaching the holder a second time completes it, whichever change arrives first
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var shared = new CycleTestNode { Name = "Shared" };
        var holder = new CycleTestNode { Name = "Holder" };
        var source = new CycleTestNode(context) { Name = "Root", Items = [holder, shared] };
        holder.Child = shared;
        var mirror = CreateMirror(source);
        var changes = CaptureChanges(context, () =>
        {
            if (reassignmentFirst)
                holder.Child = new CycleTestNode { Name = "Fresh" };

            source.Child = holder;

            if (!reassignmentFirst)
                holder.Child = new CycleTestNode { Name = "Fresh" };
        });

        // Act
        mirror.ApplySubjectUpdate(SubjectUpdate.CreatePartialUpdateFromChanges(source, changes, []), DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Assert.Equal("Shared", mirror.Items[1].Name);
        Assert.Equal("Fresh", mirror.Items[0].Child!.Name);
        Assert.Same(mirror.Items[0], mirror.Child);
    }

    private static Person CreateMirror(Person source)
    {
        var mirror = new Person(InterceptorSubjectContext.Create().WithRegistry());
        mirror.ApplySubjectUpdate(SubjectUpdate.CreateCompleteUpdate(source, []), DefaultSubjectFactory.Instance, ChangeOrigin.Local);
        return mirror;
    }

    private static CycleTestNode CreateMirror(CycleTestNode source)
    {
        var mirror = new CycleTestNode(InterceptorSubjectContext.Create().WithRegistry());
        mirror.ApplySubjectUpdate(SubjectUpdate.CreateCompleteUpdate(source, []), DefaultSubjectFactory.Instance, ChangeOrigin.Local);
        return mirror;
    }

    private static SubjectPropertyChange[] CaptureChanges(IInterceptorSubjectContext context, Action change)
    {
        var changes = new List<SubjectPropertyChange>();
        using (context.GetPropertyChangeObservable(System.Reactive.Concurrency.ImmediateScheduler.Instance).Subscribe(changes.Add))
        {
            change();
        }

        return changes.ToArray();
    }

    private static SubjectPropertyUpdate CreateValueUpdate(string value)
        => new() { Kind = SubjectPropertyUpdateKind.Value, Value = value };

    private static SubjectPropertyUpdate CreateObjectUpdate(string subjectId)
        => new() { Kind = SubjectPropertyUpdateKind.Object, Id = subjectId };
}
