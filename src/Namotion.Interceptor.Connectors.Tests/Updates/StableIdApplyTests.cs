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

        var child1Id = ((IInterceptorSubject)child1).GetOrAddSubjectId();
        var rootId = ((IInterceptorSubject)node).GetOrAddSubjectId();
        var newChildId = SubjectRegistryExtensions.GenerateSubjectId();

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
            (IInterceptorSubject)node, update, new DefaultSubjectFactory());

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

        var child2Id = ((IInterceptorSubject)child2).GetOrAddSubjectId();
        var rootId = ((IInterceptorSubject)node).GetOrAddSubjectId();

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
            (IInterceptorSubject)node, update, new DefaultSubjectFactory());

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

        var child3Id = ((IInterceptorSubject)child3).GetOrAddSubjectId();
        var rootId = ((IInterceptorSubject)node).GetOrAddSubjectId();

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
            (IInterceptorSubject)node, update, new DefaultSubjectFactory());

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

        var childId = ((IInterceptorSubject)child).GetOrAddSubjectId();

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
            (IInterceptorSubject)node, update, new DefaultSubjectFactory());

        Assert.Equal("UpdatedName", child.Name);
    }

    [Fact]
    public void ApplyInsert_WithMissingAfterId_AppendsToEnd()
    {
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var child1 = new CycleTestNode { Name = "Child1" };
        var node = new CycleTestNode(context) { Name = "Root", Items = [child1] };

        var rootId = ((IInterceptorSubject)node).GetOrAddSubjectId();
        var newChildId = SubjectRegistryExtensions.GenerateSubjectId();

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
            (IInterceptorSubject)node, update, new DefaultSubjectFactory());

        // Should append to end when afterId not found
        Assert.Equal(2, node.Items.Count);
        Assert.Equal("Child1", node.Items[0].Name);
        Assert.Equal("NewChild", node.Items[1].Name);
    }
}
