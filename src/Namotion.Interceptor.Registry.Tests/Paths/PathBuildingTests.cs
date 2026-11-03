using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Registry.Tests.Models;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Registry.Tests.Paths;

public class PathBuildingTests
{
    private static IInterceptorSubjectContext CreateContext()
    {
        return InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();
    }

    [Fact]
    public void WhenRootProperty_ThenTryGetPathReturnsPropertyName()
    {
        // Arrange
        var context = CreateContext();
        var container = new TestContainer(context) { Name = "Root" };
        var property = container.TryGetRegisteredSubject()!.TryGetProperty("Name")!;

        // Act
        var path = property.TryGetPath(DefaultPathProvider.Instance, null);

        // Assert
        Assert.Equal("Name", path);
    }

    [Fact]
    public void WhenUsingDefaultProviderOverload_ThenDelegatesToDefaultProvider()
    {
        // Arrange
        var context = CreateContext();
        var parent = new Models.Person(context) { FirstName = "Parent" };
        var child = new Models.Person(context) { FirstName = "Child" };
        parent.Father = child;
        var firstNameProperty = child.TryGetRegisteredSubject()!.TryGetProperty("FirstName")!;

        // Act - the convenience overload uses DefaultPathProvider.Instance and threads rootSubject through
        var absolute = firstNameProperty.TryGetPath();
        var relative = firstNameProperty.TryGetPath(parent);

        // Assert
        Assert.Equal("Father.FirstName", absolute);
        Assert.Equal(firstNameProperty.TryGetPath(DefaultPathProvider.Instance, null), absolute);
        Assert.Equal(firstNameProperty.TryGetPath(DefaultPathProvider.Instance, parent), relative);
    }

    [Fact]
    public void WhenNestedSubjectProperty_ThenTryGetPathReturnsDottedPath()
    {
        // Arrange
        var context = CreateContext();
        var parent = new Models.Person(context) { FirstName = "Parent" };
        var child = new Models.Person(context) { FirstName = "Child" };
        parent.Father = child;
        var firstNameProperty = child.TryGetRegisteredSubject()!.TryGetProperty("FirstName")!;

        // Act
        var path = firstNameProperty.TryGetPath(DefaultPathProvider.Instance, null);

        // Assert
        Assert.Equal("Father.FirstName", path);
    }

    [Fact]
    public void WhenCustomSeparator_ThenTryGetPathUsesSeparator()
    {
        // Arrange
        var context = CreateContext();
        var parent = new Models.Person(context) { FirstName = "Parent" };
        var child = new Models.Person(context) { FirstName = "Child" };
        parent.Father = child;
        var firstNameProperty = child.TryGetRegisteredSubject()!.TryGetProperty("FirstName")!;

        // Act
        var path = firstNameProperty.TryGetPath(new DefaultPathProvider('/'), null);

        // Assert
        Assert.Equal("Father/FirstName", path);
    }

    [Fact]
    public void WhenRootSubjectProvided_ThenTryGetPathStopsAtRoot()
    {
        // Arrange
        var context = CreateContext();
        var grandparent = new Models.Person(context) { FirstName = "Grandparent" };
        var parent = new Models.Person(context) { FirstName = "Parent" };
        var child = new Models.Person(context) { FirstName = "Child" };
        grandparent.Father = parent;
        parent.Father = child;
        var firstNameProperty = child.TryGetRegisteredSubject()!.TryGetProperty("FirstName")!;

        // Act
        var pathFromParent = firstNameProperty.TryGetPath(DefaultPathProvider.Instance, parent);
        var pathFromGrandparent = firstNameProperty.TryGetPath(DefaultPathProvider.Instance, grandparent);
        var pathAbsolute = firstNameProperty.TryGetPath(DefaultPathProvider.Instance, null);

        // Assert
        Assert.Equal("Father.FirstName", pathFromParent);
        Assert.Equal("Father.Father.FirstName", pathFromGrandparent);
        Assert.Equal("Father.Father.FirstName", pathAbsolute);
    }

    [Fact]
    public void WhenRootSubjectNotInParentChain_ThenTryGetPathReturnsNull()
    {
        // Arrange - 'unrelated' is a separate root that is not an ancestor of the property
        var context = CreateContext();
        var parent = new Models.Person(context) { FirstName = "Parent" };
        var child = new Models.Person(context) { FirstName = "Child" };
        parent.Father = child;
        var unrelated = new Models.Person(context) { FirstName = "Unrelated" };
        var firstNameProperty = child.TryGetRegisteredSubject()!.TryGetProperty("FirstName")!;

        // Act - the property is not reachable from 'unrelated', so there is no relative path
        var path = firstNameProperty.TryGetPath(DefaultPathProvider.Instance, unrelated);

        // Assert
        Assert.Null(path);
    }

    [Fact]
    public void WhenSubjectIsReachableFromRootViaSecondParent_ThenTryGetPathResolvesViaThatParent()
    {
        // Arrange - 'shared' has two parents; only the second is reachable from 'root'
        var context = CreateContext();
        var root = new Models.Person(context) { FirstName = "Root" };
        var other = new Models.Person(context) { FirstName = "Other" };
        var shared = new Models.Person(context) { FirstName = "Shared" };

        // 'shared' is referenced first by 'other' (not under root) and then by root.Father
        other.Father = shared;
        root.Father = shared;
        var firstNameProperty = shared.TryGetRegisteredSubject()!.TryGetProperty("FirstName")!;

        // Act - the first parent ('other') does not reach 'root', so the search must use the second
        var path = firstNameProperty.TryGetPath(DefaultPathProvider.Instance, root);

        // Assert
        Assert.Equal("Father.FirstName", path);
    }

    [Fact]
    public void WhenFirstParentIsOutsideTheRoot_ThenPathGoesThroughTheParentThatReachesIt()
    {
        // Arrange
        var context = CreateContext();
        var shared = new Person { FirstName = "Shared" };
        var child = new Person { Father = shared };
        _ = new Person(context)
        {
            Mother = new Person { Father = shared },
            Children = [child]
        };

        // Act
        var path = shared.TryGetRegisteredProperty(p => p.FirstName)!.TryGetPath(DefaultPathProvider.Instance, child);

        // Assert
        Assert.Equal("Father.FirstName", path);
    }

    [Fact]
    public void WhenRootSubjectIsPropertyOwner_ThenTryGetPathReturnsPropertyName()
    {
        // Arrange
        var context = CreateContext();
        var parent = new Models.Person(context) { FirstName = "Parent" };
        var child = new Models.Person(context) { FirstName = "Child" };
        parent.Father = child;
        var firstNameProperty = child.TryGetRegisteredSubject()!.TryGetProperty("FirstName")!;

        // Act
        var path = firstNameProperty.TryGetPath(DefaultPathProvider.Instance, child);

        // Assert
        Assert.Equal("FirstName", path);
    }

    [Fact]
    public void WhenSubjectParentChainHasCycle_ThenTryGetPathReturnsNull()
    {
        // Arrange - mutual Father references form a cycle in the parent chain
        var context = CreateContext();
        var a = new Models.Person(context) { FirstName = "A" };
        var b = new Models.Person(context) { FirstName = "B" };
        a.Father = b;
        b.Father = a;
        var firstNameProperty = a.TryGetRegisteredSubject()!.TryGetProperty("FirstName")!;

        // Act - a cycle has no finite path, so it is reported as null rather than throwing
        var path = firstNameProperty.TryGetPath(DefaultPathProvider.Instance, null);

        // Assert
        Assert.Null(path);
    }

    [Fact]
    public void WhenSubjectReachableFromRootViaTwoAcyclicPaths_ThenTryGetPathResolves()
    {
        // Arrange - a diamond: 'shared' is reachable from 'root' through both Father->A and Mother->B,
        // so the per-branch cycle pruning must not falsely prune the shared subject.
        var context = CreateContext();
        var root = new Models.Person(context) { FirstName = "Root" };
        var a = new Models.Person(context) { FirstName = "A" };
        var b = new Models.Person(context) { FirstName = "B" };
        var shared = new Models.Person(context) { FirstName = "Shared" };
        root.Father = a;
        root.Mother = b;
        a.Father = shared; // shared's first parent
        b.Father = shared; // shared's second parent
        var firstNameProperty = shared.TryGetRegisteredSubject()!.TryGetProperty("FirstName")!;

        // Act
        var path = firstNameProperty.TryGetPath(DefaultPathProvider.Instance, root);

        // Assert - resolves via the first parent that reaches the root
        Assert.Equal("Father.Father.FirstName", path);
    }

    [Fact]
    public void WhenSharedDeadEndIsMemoizedDuringSearch_ThenRootStillResolvesViaAnotherBranch()
    {
        // Arrange - 'leaf' has three parents. The first two (p1, p2) lead only to a shared dead-end
        // subject ('dead') that cannot reach the root, so the search explores 'dead' via p1, memoizes it
        // as unreachable, then hits that memo when reaching 'dead' again via p2. The third parent ('good')
        // reaches the root. This exercises the unreachable-memoization path and confirms a memoized
        // dead-end does not prevent the root being found through another branch.
        var context = CreateContext();
        var root = new Models.Person(context) { FirstName = "Root" };
        var good = new Models.Person(context) { FirstName = "Good" };
        var p1 = new Models.Person(context) { FirstName = "P1" };
        var p2 = new Models.Person(context) { FirstName = "P2" };
        var dead = new Models.Person(context) { FirstName = "Dead" };
        var leaf = new Models.Person(context) { FirstName = "Leaf" };

        p1.Father = leaf;   // leaf's first parent
        p2.Father = leaf;   // leaf's second parent
        good.Father = leaf; // leaf's third parent (the only one reaching the root)
        dead.Father = p1;   // p1 -> dead (dead end)
        dead.Mother = p2;   // p2 -> the same shared dead end
        root.Father = good; // good -> root
        var firstNameProperty = leaf.TryGetRegisteredSubject()!.TryGetProperty("FirstName")!;

        // Act
        var path = firstNameProperty.TryGetPath(DefaultPathProvider.Instance, root);

        // Assert
        Assert.Equal("Father.Father.FirstName", path);
    }

    [Fact]
    public void WhenParentChainHasCycleButRootReachableViaOtherBranch_ThenTryGetPathResolves()
    {
        // Arrange - 'shared' has a cyclic first parent (cyclicX <-> cyclicY) that never reaches the root,
        // and a second parent 'good' that does. The cycle on the first branch must not prevent the search
        // from finding the root through the acyclic branch.
        var context = CreateContext();
        var root = new Models.Person(context) { FirstName = "Root" };
        var good = new Models.Person(context) { FirstName = "Good" };
        var cyclicX = new Models.Person(context) { FirstName = "X" };
        var cyclicY = new Models.Person(context) { FirstName = "Y" };
        var shared = new Models.Person(context) { FirstName = "Shared" };

        cyclicX.Mother = shared;  // shared's first parent (leads into a cycle)
        good.Father = shared;     // shared's second parent (reaches the root)
        cyclicX.Father = cyclicY;
        cyclicY.Father = cyclicX; // cyclicX <-> cyclicY cycle
        root.Father = good;       // good is reachable from root
        var firstNameProperty = shared.TryGetRegisteredSubject()!.TryGetProperty("FirstName")!;

        // Act
        var path = firstNameProperty.TryGetPath(DefaultPathProvider.Instance, root);

        // Assert
        Assert.Equal("Father.Father.FirstName", path);
    }

    [Fact]
    public void WhenMultiParentChainIsCyclicAndRootUnreachable_ThenTryGetPathReturnsNull()
    {
        // Arrange - 'leaf' has two parents (forcing the multi-parent search): one into a cycle, one a
        // dead end. The provided root is reachable from neither, so the result is null (not a throw).
        var context = CreateContext();
        var unrelated = new Models.Person(context) { FirstName = "Unrelated" };
        var leaf = new Models.Person(context) { FirstName = "Leaf" };
        var cyclic = new Models.Person(context) { FirstName = "Cyclic" };
        var deadEnd = new Models.Person(context) { FirstName = "DeadEnd" };

        cyclic.Father = leaf;     // leaf's first parent
        deadEnd.Mother = leaf;    // leaf's second parent (forces the multi-parent search)
        leaf.Father = cyclic;     // leaf <-> cyclic cycle on the first branch
        var firstNameProperty = leaf.TryGetRegisteredSubject()!.TryGetProperty("FirstName")!;

        // Act
        var path = firstNameProperty.TryGetPath(DefaultPathProvider.Instance, unrelated);

        // Assert
        Assert.Null(path);
    }

    [Fact]
    public void WhenMultiParentChainIsVeryDeep_ThenTryGetPathResolvesWithoutStackOverflow()
    {
        // Arrange - a parent chain far deeper than recursion would tolerate, with a multi-parent branch at
        // the leaf to force the iterative search. Because the search uses an explicit stack (not the call
        // stack) and is not depth-bounded, it resolves the full path instead of overflowing or giving up.
        const int depth = 10_000;
        var context = CreateContext();
        var root = new Models.Person(context) { FirstName = "Root" };
        var chain = new List<Models.Person> { root };
        for (var i = 0; i < depth; i++)
        {
            var next = new Models.Person(context) { FirstName = $"P{i}" };
            chain[^1].Father = next; // chain[i] is the parent of chain[i+1]
            chain.Add(next);
        }

        var leaf = chain[^1];
        var extra = new Models.Person(context) { FirstName = "Extra" };
        extra.Mother = leaf; // second parent for the leaf forces the multi-parent search

        var firstNameProperty = leaf.TryGetRegisteredSubject()!.TryGetProperty("FirstName")!;

        // Act
        var path = firstNameProperty.TryGetPath(DefaultPathProvider.Instance, root);

        // Assert - resolves to "Father" repeated for each hop up the chain, then the leaf property
        Assert.NotNull(path);
        var segments = path!.Split('.');
        Assert.Equal(depth + 1, segments.Length);
        Assert.Equal("FirstName", segments[^1]);
        Assert.All(segments[..^1], segment => Assert.Equal("Father", segment));
    }

    [Fact]
    public void WhenPropertyIsCollectionElement_ThenDefaultProviderPathIncludesBracketIndex()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var root = new TryGetPathIndexRoot(context);
        root.Items = [new TryGetPathIndexChild(context), new TryGetPathIndexChild(context)];
        var nameProperty = root.Items[1].TryGetRegisteredSubject()!.TryGetProperty("Name")!;

        // Act
        var path = nameProperty.TryGetPath(DefaultPathProvider.Instance, root);

        // Assert
        Assert.Equal("Items[1].Name", path);
    }
}

[Namotion.Interceptor.Attributes.InterceptorSubject]
public partial class TryGetPathIndexRoot
{
    public partial TryGetPathIndexChild[] Items { get; set; }

    public TryGetPathIndexRoot()
    {
        Items = [];
    }
}

[Namotion.Interceptor.Attributes.InterceptorSubject]
public partial class TryGetPathIndexChild
{
    public partial string Name { get; set; }

    public TryGetPathIndexChild()
    {
        Name = "";
    }
}
