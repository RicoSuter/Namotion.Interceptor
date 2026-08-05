using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Connectors.Tests;

public class SourceScopeTests
{
    private static IInterceptorSubjectContext CreateContext() =>
        InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithLifecycle()
            .WithParents()
            .WithSourceMonitoring();

    [Fact]
    public void WhenTheSourceIsRootedAtAnAncestor_ThenItIsInScope()
    {
        // Arrange
        var context = CreateContext();
        var root = new Person(context);
        var child = new Person();
        root.Mother = child;
        var source = new TestStateSource(root);

        // Act
        var inScope = SourceScope.IsInScope(source, child);

        // Assert
        Assert.True(inScope);
    }

    [Fact]
    public void WhenTheSourceIsRootedAtADescendant_ThenItIsInScope()
    {
        // Arrange
        var context = CreateContext();
        var root = new Person(context);
        var child = new Person();
        root.Mother = child;
        var source = new TestStateSource(child);

        // Act
        var inScope = SourceScope.IsInScope(source, root);

        // Assert
        Assert.True(inScope);
    }

    [Fact]
    public void WhenTheSourceIsRootedOnASiblingBranch_ThenItIsNotInScope()
    {
        // Arrange
        var context = CreateContext();
        var root = new Person(context);
        var left = new Person();
        var right = new Person();
        root.Mother = left;
        root.Father = right;
        var source = new TestStateSource(right);

        // Act
        var inScope = SourceScope.IsInScope(source, left);

        // Assert
        Assert.False(inScope);
    }

    [Fact]
    public void WhenTheAnchorIsTheSourceRootItself_ThenItIsInScopeWithoutAnyParentWalk()
    {
        // Arrange
        var context = CreateContext();
        var detached = new Person(context);
        var source = new TestStateSource(detached);

        // Act
        var inScope = SourceScope.IsInScope(source, detached);

        // Assert
        Assert.True(inScope);
    }

    [Fact]
    public void WhenTheSubjectHasTwoParents_ThenSourcesOnEitherPathAreInScope()
    {
        // Arrange
        var context = CreateContext();
        var firstRoot = new Person(context);
        var secondRoot = new Person(context);
        var shared = new Person();
        firstRoot.Mother = shared;
        secondRoot.Mother = shared;

        // Act & Assert
        Assert.True(SourceScope.IsInScope(new TestStateSource(firstRoot), shared));
        Assert.True(SourceScope.IsInScope(new TestStateSource(secondRoot), shared));
    }
}
