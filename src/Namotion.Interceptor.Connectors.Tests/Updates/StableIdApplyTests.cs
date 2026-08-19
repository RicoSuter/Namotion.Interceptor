using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.Connectors.Updates.Internal;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Connectors.Tests.Updates;

[Collection(SubjectUpdateDiagnosticsCollection.Name)]
public class StableIdApplyTests
{
    [Fact]
    public void WhenPartialUpdateHasNoRoot_ThenSubjectsAreResolvedByStableId()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var child = new CycleTestNode { Name = "OriginalName" };
        var node = new CycleTestNode(context) { Name = "Root", Items = [child] };

        var childId = child.GetOrAddSubjectId();

        var update = new SubjectUpdate
        {
            Root = null, // Partial update without root
            Subjects = new()
            {
                [childId] = new()
                {
                    ["Name"] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Value,
                        Value = "UpdatedName"
                    }
                }
            }
        };

        // Act
        SubjectUpdateApplier.ApplyUpdate(
            node, update, new DefaultSubjectFactory(), ChangeOrigin.Local);

        // Assert
        Assert.Equal("UpdatedName", child.Name);
    }

    [Fact]
    public void WhenObjectUpdateHasSameSubjectId_ThenExistingInstanceIsKept()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var child = new CycleTestNode { Name = "OriginalName" };
        var node = new CycleTestNode(context) { Name = "Root", Child = child };

        var childId = child.GetOrAddSubjectId();
        var rootId = node.GetOrAddSubjectId();

        // Update with same subject ID - should keep existing CLR object
        var update = new SubjectUpdate
        {
            Root = rootId,
            Subjects = new()
            {
                [rootId] = new()
                {
                    ["Child"] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Object,
                        Id = childId
                    }
                },
                [childId] = new()
                {
                    ["Name"] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Value,
                        Value = "UpdatedName"
                    }
                }
            }
        };

        // Act
        SubjectUpdateApplier.ApplyUpdate(
            node, update, new DefaultSubjectFactory(), ChangeOrigin.Local);

        // Assert
        Assert.Same(child, node.Child);
        Assert.Equal("UpdatedName", node.Child!.Name);
    }

    [Fact]
    public void WhenObjectUpdateHasDifferentSubjectId_ThenInstanceIsReplaced()
    {
        // Arrange
        var sourceContext = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var targetContext = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();

        var originalChild = new CycleTestNode { Name = "OriginalChild" };
        var node = new CycleTestNode(targetContext) { Name = "Root", Child = originalChild };

        var rootId = node.GetOrAddSubjectId();
        var originalChildId = originalChild.GetOrAddSubjectId();

        // Create a replacement child on the source side with a different ID
        var replacementChild = new CycleTestNode(sourceContext) { Name = "ReplacementChild" };
        var replacementChildId = replacementChild.GetOrAddSubjectId();

        Assert.NotEqual(originalChildId, replacementChildId);

        var update = new SubjectUpdate
        {
            Root = rootId,
            Subjects = new()
            {
                [rootId] = new()
                {
                    ["Child"] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Object,
                        Id = replacementChildId
                    }
                },
                [replacementChildId] = new()
                {
                    ["Name"] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Value,
                        Value = "ReplacementChild"
                    }
                }
            }
        };

        // Act
        SubjectUpdateApplier.ApplyUpdate(
            node, update, new DefaultSubjectFactory(), ChangeOrigin.Local);

        // Assert
        Assert.NotSame(originalChild, node.Child);
        Assert.Equal("ReplacementChild", node.Child!.Name);
        Assert.Equal(replacementChildId, node.Child.TryGetSubjectId());
    }

    [Fact]
    public void WhenStructuralChangeDetachesSubject_ThenPropertyUpdatesAreStillApplied()
    {
        // Arrange: root → child via object ref
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var child = new CycleTestNode { Name = "OriginalName" };
        var root = new CycleTestNode(context) { Name = "Root", Child = child };

        var rootId = root.GetOrAddSubjectId();
        var childId = child.GetOrAddSubjectId();

        // Build an update that BOTH removes child (structural) AND updates child's Name (value).
        // This happens when CQP batches a value change and a structural change together:
        //   1. Sender sets child.Name = "UpdatedName"
        //   2. Sender sets root.Child = null
        //   3. Both changes are flushed in the same CQP batch
        var update = new SubjectUpdate
        {
            Root = rootId,
            Subjects = new()
            {
                [rootId] = new()
                {
                    ["Child"] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Object,
                        Id = null // Remove child
                    }
                },
                [childId] = new()
                {
                    ["Name"] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Value,
                        Value = "UpdatedName"
                    }
                }
            }
        };

        // Act
        SubjectUpdateApplier.ApplyUpdate(root, update, new DefaultSubjectFactory(), ChangeOrigin.Local);

        // Assert
        Assert.Null(root.Child); // structural change applied
        Assert.Equal("UpdatedName", child.Name); // value applied despite detach
    }

    [Fact]
    public void WhenUpdateHasRootScalarAndNonRootChange_ThenBothAreApplied()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var child = new CycleTestNode { Name = "ChildOriginal" };
        var node = new CycleTestNode(context) { Name = "RootOriginal", Child = child };

        var rootId = node.GetOrAddSubjectId();
        var childId = child.GetOrAddSubjectId();

        // Root scalar change + non-root change in same update
        var update = new SubjectUpdate
        {
            Root = rootId,
            Subjects = new()
            {
                [rootId] = new()
                {
                    ["Name"] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Value,
                        Value = "RootUpdated"
                    }
                },
                [childId] = new()
                {
                    ["Name"] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Value,
                        Value = "ChildUpdated"
                    }
                }
            }
        };

        // Act
        SubjectUpdateApplier.ApplyUpdate(
            node, update, new DefaultSubjectFactory(), ChangeOrigin.Local);

        // Assert
        Assert.Equal("RootUpdated", node.Name);
        Assert.Equal("ChildUpdated", child.Name);
    }

    [Fact]
    public void WhenUpdateAddsNewDictionaryItem_ThenPropertiesAreAppliedBeforeGraphEntry()
    {
        // Arrange: root with empty dict
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var root = new CycleTestNode(context) { Name = "Root" };
        var rootId = root.GetOrAddSubjectId();

        // Update adds a new dict item with properties - the new subject should have
        // values applied BEFORE entering the graph (no interceptors on new instance).
        var newItemId = "new-dict-item-id";
        var update = new SubjectUpdate
        {
            Root = rootId,
            Subjects = new()
            {
                [rootId] = new()
                {
                    ["Lookup"] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Dictionary,
                        Items =
                        [
                            new SubjectPropertyItemUpdate { Id = newItemId, Key = "key1" }
                        ]
                    }
                },
                [newItemId] = new()
                {
                    ["Name"] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Value,
                        Value = "NewItemName"
                    }
                }
            },
            CompleteSubjectIds = [newItemId]
        };

        // Act
        SubjectUpdateApplier.ApplyUpdate(root, update, new DefaultSubjectFactory(), ChangeOrigin.Local);

        // Assert: new dict item exists with property value applied
        Assert.Single(root.Lookup);
        Assert.Equal("NewItemName", root.Lookup["key1"].Name);
        Assert.Equal(newItemId, root.Lookup["key1"].TryGetSubjectId());
    }

    [Fact]
    public void WhenUpdateAddsNewCollectionItem_ThenPropertiesAreAppliedBeforeGraphEntry()
    {
        // Arrange: root with empty collection
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var root = new CycleTestNode(context) { Name = "Root" };
        var rootId = root.GetOrAddSubjectId();

        var newItemId = "new-collection-item-id";
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
                            new SubjectPropertyItemUpdate { Id = newItemId }
                        ]
                    }
                },
                [newItemId] = new()
                {
                    ["Name"] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Value,
                        Value = "NewCollectionItem"
                    }
                }
            },
            CompleteSubjectIds = [newItemId]
        };

        // Act
        SubjectUpdateApplier.ApplyUpdate(root, update, new DefaultSubjectFactory(), ChangeOrigin.Local);

        // Assert
        Assert.Single(root.Items);
        Assert.Equal("NewCollectionItem", root.Items.First().Name);
    }

    [Fact]
    public void WhenUpdateAddsNewObjectReference_ThenPropertiesAreAppliedBeforeGraphEntry()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var root = new CycleTestNode(context) { Name = "Root" };
        var rootId = root.GetOrAddSubjectId();

        var newChildId = "new-objectref-child-id";
        var update = new SubjectUpdate
        {
            Root = rootId,
            Subjects = new()
            {
                [rootId] = new()
                {
                    ["Child"] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Object,
                        Id = newChildId
                    }
                },
                [newChildId] = new()
                {
                    ["Name"] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Value,
                        Value = "NewChild"
                    }
                }
            },
            CompleteSubjectIds = [newChildId]
        };

        // Act
        SubjectUpdateApplier.ApplyUpdate(root, update, new DefaultSubjectFactory(), ChangeOrigin.Local);

        // Assert
        Assert.NotNull(root.Child);
        Assert.Equal("NewChild", root.Child!.Name);
        Assert.Equal(newChildId, root.Child.TryGetSubjectId());
    }

    [Fact]
    public void WhenSubjectMovesBetweenCollectionAndObjectReference_ThenItStaysRegistered()
    {
        // Arrange: root with subject X in Items collection
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var x = new CycleTestNode { Name = "X" };
        var root = new CycleTestNode(context)
        {
            Name = "Root",
            Items = [x],
        };

        var rootId = root.GetOrAddSubjectId();
        var xId = x.GetOrAddSubjectId();
        var idRegistry = context.GetService<ISubjectIdRegistry>();

        // Verify X is registered before apply
        Assert.True(idRegistry.TryGetSubjectById(xId, out _));

        // Build update that moves X from Items to Child (different structural property)
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
                        Items = [] // Remove all items (removes X)
                    },
                    ["Child"] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Object,
                        Id = xId // Add X as ObjectRef
                    }
                },
                [xId] = new()
                {
                    ["Name"] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Value,
                        Value = "Moved"
                    }
                }
            }
        };

        // Act
        SubjectUpdateApplier.ApplyUpdate(root, update, new DefaultSubjectFactory(), ChangeOrigin.Local);

        // Assert: X should still be registered (never temporarily unregistered)
        Assert.True(idRegistry.TryGetSubjectById(xId, out var resolved));
        Assert.Same(x, resolved);
        Assert.Equal("Moved", x.Name);
        Assert.Same(x, root.Child);
        Assert.Empty(root.Items);
    }

    [Fact]
    public void WhenNewDictionaryItemHasNestedObjectReference_ThenFullSubgraphIsPopulated()
    {
        // Arrange: verifies that nested structural properties on new subjects
        // are also populated before graph entry (recursive subgraph build).
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var root = new CycleTestNode(context) { Name = "Root" };
        var rootId = root.GetOrAddSubjectId();

        var dictItemId = "dict-item-id";
        var nestedChildId = "nested-child-id";

        var update = new SubjectUpdate
        {
            Root = rootId,
            Subjects = new()
            {
                [rootId] = new()
                {
                    ["Lookup"] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Dictionary,
                        Items =
                        [
                            new SubjectPropertyItemUpdate { Id = dictItemId, Key = "k1" }
                        ]
                    }
                },
                [dictItemId] = new()
                {
                    ["Name"] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Value,
                        Value = "DictItem"
                    },
                    ["Child"] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Object,
                        Id = nestedChildId
                    }
                },
                [nestedChildId] = new()
                {
                    ["Name"] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Value,
                        Value = "NestedChild"
                    }
                }
            },
            CompleteSubjectIds = [dictItemId, nestedChildId]
        };

        // Act
        SubjectUpdateApplier.ApplyUpdate(root, update, new DefaultSubjectFactory(), ChangeOrigin.Local);

        // Assert: full subgraph populated
        Assert.Single(root.Lookup);
        var dictItem = root.Lookup["k1"];
        Assert.Equal("DictItem", dictItem.Name);
        Assert.NotNull(dictItem.Child);
        Assert.Equal("NestedChild", dictItem.Child!.Name);
    }

    [Fact]
    public void WhenValueForUnresolvableSubjectArrives_ThenUpdateIsDroppedAndCounted()
    {
        // Arrange - a value-only update for a subject that is not in the graph and is not creatable
        // (no structural parent, not marked complete). There is no pending-apply buffer: the update
        // is dropped and counted, and convergence comes from the next complete-state update.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var root = new CycleTestNode(context) { Name = "Root" };
        var rootId = root.GetOrAddSubjectId();
        const string ghostId = "ghost-child-id";

        var droppedBefore = SubjectUpdateDiagnostics.DroppedInboundSubjectUpdates;

        var unresolvableUpdate = new SubjectUpdate
        {
            Root = null,
            Subjects = new()
            {
                [ghostId] = new()
                {
                    ["Name"] = new SubjectPropertyUpdate { Kind = SubjectPropertyUpdateKind.Value, Value = "Dropped" }
                }
            }
        };

        // Act
        SubjectUpdateApplier.ApplyUpdate(root, unresolvableUpdate, new DefaultSubjectFactory(), ChangeOrigin.Local);

        // Assert - nothing was created and the drop is counted
        Assert.Null(root.Child);
        Assert.True(
            SubjectUpdateDiagnostics.DroppedInboundSubjectUpdates >= droppedBefore + 1,
            "The drop must be counted. These counters are process-wide, so other tests running in "
            + "parallel can also increment them; assert the floor, not an exact delta.");

        // Act - a later complete-state update for the same ID converges the receiver
        var completeUpdate = new SubjectUpdate
        {
            Root = rootId,
            Subjects = new()
            {
                [rootId] = new()
                {
                    ["Child"] = new SubjectPropertyUpdate { Kind = SubjectPropertyUpdateKind.Object, Id = ghostId }
                },
                [ghostId] = new()
                {
                    ["Name"] = new SubjectPropertyUpdate { Kind = SubjectPropertyUpdateKind.Value, Value = "Converged" }
                }
            },
            CompleteSubjectIds = [ghostId]
        };
        SubjectUpdateApplier.ApplyUpdate(root, completeUpdate, new DefaultSubjectFactory(), ChangeOrigin.Local);

        // Assert - the dropped value is never recovered, the complete state is what converges
        Assert.NotNull(root.Child);
        Assert.Equal(ghostId, root.Child!.TryGetSubjectId());
        Assert.Equal("Converged", root.Child.Name);
    }

    [Fact]
    public void WhenRootIdDiffersFromTheLocalRootId_ThenNoDropIsCounted()
    {
        // Arrange - Root is a mapping hint, not an identity assignment, so the sender's root ID never
        // resolves in the receiver's registry. The root's properties are applied through that hint,
        // so the drop tripwire must stay flat: a counter that rises during healthy operation cannot
        // signal a real convergence gap.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var root = new CycleTestNode(context) { Name = "RootOriginal" };
        var localRootId = root.GetOrAddSubjectId();
        const string senderRootId = "sender-side-root-id";

        Assert.NotEqual(senderRootId, localRootId);

        var update = new SubjectUpdate
        {
            Root = senderRootId,
            Subjects = new()
            {
                [senderRootId] = new()
                {
                    ["Name"] = new SubjectPropertyUpdate { Kind = SubjectPropertyUpdateKind.Value, Value = "RootUpdated" }
                }
            }
        };

        var droppedBefore = SubjectUpdateDiagnostics.DroppedInboundSubjectUpdates;

        // Act
        SubjectUpdateApplier.ApplyUpdate(root, update, new DefaultSubjectFactory(), ChangeOrigin.Local);

        // Assert
        Assert.Equal("RootUpdated", root.Name);
        Assert.Equal(droppedBefore, SubjectUpdateDiagnostics.DroppedInboundSubjectUpdates);
    }

    [Fact]
    public void WhenDeferredSubjectIsCreatedByALaterStructuralEntry_ThenNoDropIsCounted()
    {
        // Arrange - the new grandchild is listed before the entry that creates it, so it is
        // unresolvable at its own turn and lands in the deferred pass. By the time that pass runs it
        // has been created and applied, so nothing was dropped and the tripwire must stay flat.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var child = new CycleTestNode { Name = "Child" };
        var root = new CycleTestNode(context) { Name = "Root", Child = child };
        var childId = child.GetOrAddSubjectId();
        const string grandChildId = "grand-child-id";

        var update = new SubjectUpdate
        {
            Root = null,
            Subjects = new()
            {
                [grandChildId] = new()
                {
                    ["Name"] = new SubjectPropertyUpdate { Kind = SubjectPropertyUpdateKind.Value, Value = "GrandChild" }
                },
                [childId] = new()
                {
                    ["Child"] = new SubjectPropertyUpdate { Kind = SubjectPropertyUpdateKind.Object, Id = grandChildId }
                }
            },
            CompleteSubjectIds = [grandChildId]
        };

        var droppedBefore = SubjectUpdateDiagnostics.DroppedInboundSubjectUpdates;

        // Act
        SubjectUpdateApplier.ApplyUpdate(root, update, new DefaultSubjectFactory(), ChangeOrigin.Local);

        // Assert
        Assert.NotNull(child.Child);
        Assert.Equal("GrandChild", child.Child!.Name);
        Assert.Equal(droppedBefore, SubjectUpdateDiagnostics.DroppedInboundSubjectUpdates);
    }

    [Fact]
    public void WhenNewObjectReferenceCarriesAnAttribute_ThenTheAttributeIsApplied()
    {
        // Arrange - a new subject is populated before it enters the graph, where the registry cannot
        // map an attribute name to its backing property yet. Its attributes must still land, because
        // nothing re-applies them once it is rooted.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var root = new CycleTestNode(context) { Name = "Root" };
        var rootId = root.GetOrAddSubjectId();
        const string newChildId = "new-child-with-attribute";

        var update = new SubjectUpdate
        {
            Root = rootId,
            Subjects = new()
            {
                [rootId] = new()
                {
                    ["Child"] = new SubjectPropertyUpdate { Kind = SubjectPropertyUpdateKind.Object, Id = newChildId }
                },
                [newChildId] = new()
                {
                    ["Name"] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Value,
                        Value = "NewChild",
                        Attributes = new()
                        {
                            ["Status"] = new SubjectPropertyUpdate
                            {
                                Kind = SubjectPropertyUpdateKind.Value,
                                Value = "inactive"
                            }
                        }
                    }
                }
            },
            CompleteSubjectIds = [newChildId]
        };

        // Act
        SubjectUpdateApplier.ApplyUpdate(root, update, new DefaultSubjectFactory(), ChangeOrigin.Local);

        // Assert
        Assert.NotNull(root.Child);
        Assert.Equal("NewChild", root.Child!.Name);
        Assert.Equal("inactive", root.Child.Name_Status);
    }

    [Fact]
    public void WhenNewCollectionItemCarriesAnAttribute_ThenTheAttributeIsApplied()
    {
        // Arrange - same hole as for object references, reached through the collection applier.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var root = new CycleTestNode(context) { Name = "Root" };
        var rootId = root.GetOrAddSubjectId();
        const string newItemId = "new-item-with-attribute";

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
                        Items = [new SubjectPropertyItemUpdate { Id = newItemId }]
                    }
                },
                [newItemId] = new()
                {
                    ["Name"] = new SubjectPropertyUpdate
                    {
                        Kind = SubjectPropertyUpdateKind.Value,
                        Value = "NewItem",
                        Attributes = new()
                        {
                            ["Status"] = new SubjectPropertyUpdate
                            {
                                Kind = SubjectPropertyUpdateKind.Value,
                                Value = "inactive"
                            }
                        }
                    }
                }
            },
            CompleteSubjectIds = [newItemId]
        };

        // Act
        SubjectUpdateApplier.ApplyUpdate(root, update, new DefaultSubjectFactory(), ChangeOrigin.Local);

        // Assert
        var item = Assert.Single(root.Items);
        Assert.Equal("NewItem", item.Name);
        Assert.Equal("inactive", item.Name_Status);
    }
}
