using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.Connectors.Updates.Internal;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Connectors.Tests.Updates;

public class StableIdApplyTests
{
    [Fact]
    public void ApplyCollectionInsert_WithAfterId_InsertsAtCorrectPosition()
    {
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var child1 = new CycleTestNode { Name = "Child1" };
        var child2 = new CycleTestNode { Name = "Child2" };
        var node = new CycleTestNode(context) { Name = "Root", Items = [child1, child2] };

        var child1Id = child1.GetOrAddSubjectId();
        var rootId = node.GetOrAddSubjectId();
        var newChild = new CycleTestNode { Name = "NewChild" };
        var newChildId = newChild.GetOrAddSubjectId();

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
                        Count = 3,
                        Operations =
                        [
                            new SubjectCollectionOperation
                            {
                                Action = SubjectCollectionOperationType.Insert,
                                Id = newChildId,
                                AfterId = child1Id
                            }
                        ]
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
            }
        };

        SubjectUpdateApplier.ApplyUpdate(
            node, update, new DefaultSubjectFactory());

        Assert.Equal(3, node.Items.Count);
        Assert.Equal("Child1", node.Items[0].Name);
        Assert.Equal("NewChild", node.Items[1].Name);
        Assert.Equal("Child2", node.Items[2].Name);
    }

    [Fact]
    public void ApplyCollectionRemove_ByStableId_RemovesCorrectItem()
    {
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var child1 = new CycleTestNode { Name = "Child1" };
        var child2 = new CycleTestNode { Name = "Child2" };
        var child3 = new CycleTestNode { Name = "Child3" };
        var node = new CycleTestNode(context) { Name = "Root", Items = [child1, child2, child3] };

        var child2Id = child2.GetOrAddSubjectId();
        var rootId = node.GetOrAddSubjectId();

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
                        Count = 2,
                        Operations =
                        [
                            new SubjectCollectionOperation
                            {
                                Action = SubjectCollectionOperationType.Remove,
                                Id = child2Id
                            }
                        ]
                    }
                }
            }
        };

        SubjectUpdateApplier.ApplyUpdate(
            node, update, new DefaultSubjectFactory());

        Assert.Equal(2, node.Items.Count);
        Assert.Equal("Child1", node.Items[0].Name);
        Assert.Equal("Child3", node.Items[1].Name);
    }

    [Fact]
    public void ApplyCollectionMove_ByStableId_ReordersCorrectly()
    {
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var child1 = new CycleTestNode { Name = "A" };
        var child2 = new CycleTestNode { Name = "B" };
        var child3 = new CycleTestNode { Name = "C" };
        var node = new CycleTestNode(context) { Name = "Root", Items = [child1, child2, child3] };

        var child3Id = child3.GetOrAddSubjectId();
        var rootId = node.GetOrAddSubjectId();

        // Move C to head (before A)
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
                        Count = 3,
                        Operations =
                        [
                            new SubjectCollectionOperation
                            {
                                Action = SubjectCollectionOperationType.Move,
                                Id = child3Id,
                                AfterId = null // place at head
                            }
                        ]
                    }
                }
            }
        };

        SubjectUpdateApplier.ApplyUpdate(
            node, update, new DefaultSubjectFactory());

        Assert.Equal(3, node.Items.Count);
        Assert.Equal("C", node.Items[0].Name);
        Assert.Equal("A", node.Items[1].Name);
        Assert.Equal("B", node.Items[2].Name);
    }

    [Fact]
    public void ApplyPartialUpdate_WithoutRoot_AppliesByStableIdLookup()
    {
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

        SubjectUpdateApplier.ApplyUpdate(
            node, update, new DefaultSubjectFactory());

        Assert.Equal("UpdatedName", child.Name);
    }

    [Fact]
    public void ApplyCollectionInsert_DuplicateId_IsSkipped()
    {
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var child1 = new CycleTestNode { Name = "Child1" };
        var node = new CycleTestNode(context) { Name = "Root", Items = [child1] };

        var child1Id = child1.GetOrAddSubjectId();
        var rootId = node.GetOrAddSubjectId();

        // Insert with the same ID as an existing item — should be skipped
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
                        Count = 1,
                        Operations =
                        [
                            new SubjectCollectionOperation
                            {
                                Action = SubjectCollectionOperationType.Insert,
                                Id = child1Id,
                                AfterId = null
                            }
                        ]
                    }
                }
            }
        };

        SubjectUpdateApplier.ApplyUpdate(
            node, update, new DefaultSubjectFactory());

        var singleItem = Assert.Single(node.Items);
        Assert.Equal("Child1", singleItem.Name);
    }

    [Fact]
    public void ApplyObjectUpdate_SameSubjectId_KeepsExistingInstance()
    {
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var child = new CycleTestNode { Name = "OriginalName" };
        var node = new CycleTestNode(context) { Name = "Root", Child = child };

        var childId = child.GetOrAddSubjectId();
        var rootId = node.GetOrAddSubjectId();

        // Update with same subject ID — should keep existing CLR object
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

        SubjectUpdateApplier.ApplyUpdate(
            node, update, new DefaultSubjectFactory());

        Assert.Same(child, node.Child);
        Assert.Equal("UpdatedName", node.Child!.Name);
    }

    [Fact]
    public void ApplyObjectUpdate_DifferentSubjectId_ReplacesWithNewInstance()
    {
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

        SubjectUpdateApplier.ApplyUpdate(
            node, update, new DefaultSubjectFactory());

        Assert.NotSame(originalChild, node.Child);
        Assert.Equal("ReplacementChild", node.Child!.Name);
        Assert.Equal(replacementChildId, node.Child.TryGetSubjectId());
    }

    [Fact]
    public void ApplyPartialUpdate_RootScalarAndNonRootChange_BothApplied()
    {
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

        SubjectUpdateApplier.ApplyUpdate(
            node, update, new DefaultSubjectFactory());

        Assert.Equal("RootUpdated", node.Name);
        Assert.Equal("ChildUpdated", child.Name);
    }

    [Fact]
    public void ApplyInsert_WithMissingAfterId_AppendsToEnd()
    {
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var child1 = new CycleTestNode { Name = "Child1" };
        var node = new CycleTestNode(context) { Name = "Root", Items = [child1] };

        var rootId = node.GetOrAddSubjectId();
        var newChild = new CycleTestNode { Name = "NewChild" };
        var newChildId = newChild.GetOrAddSubjectId();

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
                        Count = 2,
                        Operations =
                        [
                            new SubjectCollectionOperation
                            {
                                Action = SubjectCollectionOperationType.Insert,
                                Id = newChildId,
                                AfterId = "nonexistent_id" // missing predecessor
                            }
                        ]
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
            }
        };

        SubjectUpdateApplier.ApplyUpdate(
            node, update, new DefaultSubjectFactory());

        // Should append to end when afterId not found
        Assert.Equal(2, node.Items.Count);
        Assert.Equal("Child1", node.Items[0].Name);
        Assert.Equal("NewChild", node.Items[1].Name);
    }
}
