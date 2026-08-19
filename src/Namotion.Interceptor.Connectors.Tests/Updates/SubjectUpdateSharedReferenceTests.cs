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

    private static SubjectPropertyUpdate CreateValueUpdate(string value)
        => new() { Kind = SubjectPropertyUpdateKind.Value, Value = value };

    private static SubjectPropertyUpdate CreateObjectUpdate(string subjectId)
        => new() { Kind = SubjectPropertyUpdateKind.Object, Id = subjectId };
}
