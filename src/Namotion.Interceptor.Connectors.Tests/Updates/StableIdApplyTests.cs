using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.Connectors.Updates.Internal;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Connectors.Tests.Updates;

public class StableIdApplyTests
{
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
}
