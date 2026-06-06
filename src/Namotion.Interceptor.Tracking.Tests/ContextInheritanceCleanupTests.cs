using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests;

public class ContextInheritanceCleanupTests
{
    [Fact]
    public void WhenDetachingChildCreatedWithRootContext_ThenRootContextFallbackIsRemoved()
    {
        // Arrange
        var rootContext = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        var parent = new Person(rootContext);
        var child = new Person(rootContext);
        parent.Mother = child;

        // Act
        parent.Mother = null;

        // Assert
        Assert.Empty(child.GetServices<ILifecycleInterceptor>());
    }

    [Fact]
    public void WhenDetachingChildCreatedWithoutContext_ThenNoServicesRemain()
    {
        // Arrange
        var rootContext = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        var parent = new Person(rootContext);
        var child = new Person();
        parent.Mother = child;

        // Act
        parent.Mother = null;

        // Assert
        Assert.Empty(child.GetServices<ILifecycleInterceptor>());
    }

    [Fact]
    public void WhenDetachingFromDeepTree_ThenAllAncestorContextsAreRemoved()
    {
        // Arrange
        var rootContext = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        var grandparent = new Person(rootContext);
        var parent = new Person();
        var child = new Person(rootContext);

        grandparent.Mother = parent;
        parent.Mother = child;

        // Act
        parent.Mother = null;

        // Assert
        Assert.Empty(child.GetServices<ILifecycleInterceptor>());
    }

    [Fact]
    public void WhenDetachingSubtree_ThenAllDescendantsLoseAncestorContexts()
    {
        // Arrange
        var rootContext = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        var root = new Person(rootContext);
        var parent = new Person(rootContext);
        var child = new Person(rootContext);

        root.Mother = parent;
        parent.Mother = child;

        // Act
        root.Mother = null;

        // Assert
        Assert.Empty(parent.GetServices<ILifecycleInterceptor>());
        Assert.Empty(child.GetServices<ILifecycleInterceptor>());
    }

    [Fact]
    public void WhenDetaching_ThenIndependentFallbackContextIsPreserved()
    {
        // Arrange
        var rootContext = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        var independentContext = InterceptorSubjectContext.Create();
        independentContext.AddService(42);

        var parent = new Person(rootContext);
        var child = new Person();
        ((IInterceptorSubject)child).Context.AddFallbackContext(independentContext);
        parent.Mother = child;

        // Act
        parent.Mother = null;

        // Assert
        Assert.Empty(child.GetServices<ILifecycleInterceptor>());
        Assert.Contains(42, child.GetServices<int>());
    }

    [Fact]
    public void WhenMovingBetweenParents_ThenChildGetsNewParentServices()
    {
        // Arrange
        var rootContext = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        var parentA = new Person(rootContext);
        var parentB = new Person(rootContext);
        var child = new Person(rootContext);

        parentA.Mother = child;

        // Act
        parentA.Mother = null;
        parentB.Mother = child;

        // Assert
        Assert.NotEmpty(child.GetServices<ILifecycleInterceptor>());
    }

    [Fact]
    public void WhenMovingBetweenParents_ThenServicesResolveCorrectly()
    {
        // Arrange
        var rootContext = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        var parentA = new Person(rootContext);
        var parentB = new Person(rootContext);
        var child = new Person();

        parentA.Mother = child;

        // Act
        parentA.Mother = null;
        parentB.Mother = child;

        // Assert
        Assert.Equal(
            rootContext.GetServices<ILifecycleInterceptor>(),
            child.GetServices<ILifecycleInterceptor>());
    }

    [Fact]
    public void WhenDetachingChildWithChildSpecificService_ThenOwnServiceSurvivesButInheritedLost()
    {
        // Arrange
        var rootContext = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        var parent = new Person(rootContext);
        var child = new Person();
        parent.Mother = child;

        ((IInterceptorSubject)child).Context.AddService("child-service");

        // Act
        parent.Mother = null;

        // Assert
        Assert.Empty(child.GetServices<ILifecycleInterceptor>());
        Assert.Contains("child-service", child.GetServices<string>());
    }

    [Fact]
    public void WhenDetachingArray_ThenAllChildrenLoseAncestorContexts()
    {
        // Arrange
        var rootContext = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        var parent = new Person(rootContext);
        var child1 = new Person(rootContext);
        var child2 = new Person(rootContext);
        parent.Children = [child1, child2];

        // Act
        parent.Children = [];

        // Assert
        Assert.Empty(child1.GetServices<ILifecycleInterceptor>());
        Assert.Empty(child2.GetServices<ILifecycleInterceptor>());
    }

    [Fact]
    public void WhenParentHasCustomService_ThenDetachedChildLosesIt()
    {
        // Arrange
        var rootContext = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        var parent = new Person(rootContext);
        ((IInterceptorSubject)parent).Context.AddService("parent-service");

        var child = new Person();
        parent.Mother = child;

        Assert.Contains("parent-service", child.GetServices<string>());

        // Act
        parent.Mother = null;

        // Assert
        Assert.Empty(child.GetServices<string>());
    }

    [Fact]
    public void WhenRootSubjectIsNeverDetached_ThenContextRemainsIntact()
    {
        // Arrange
        var rootContext = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking();

        var root = new Person(rootContext);
        var child = new Person();
        root.Mother = child;

        // Act
        root.Mother = null;

        // Assert
        Assert.NotEmpty(root.GetServices<ILifecycleInterceptor>());
    }

    [Fact]
    public void WhenGetFallbackContexts_ThenReturnsDirectFallbacks()
    {
        // Arrange
        var context1 = InterceptorSubjectContext.Create();
        var context2 = InterceptorSubjectContext.Create();
        var context3 = InterceptorSubjectContext.Create();

        context1.AddFallbackContext(context2);
        context1.AddFallbackContext(context3);

        // Act
        var fallbacks = context1.GetFallbackContexts();

        // Assert
        Assert.Equal(2, fallbacks.Count);
        Assert.Contains(context2, fallbacks);
        Assert.Contains(context3, fallbacks);
    }

    [Fact]
    public void WhenGetFallbackContextsAfterRemove_ThenReturnsUpdatedSet()
    {
        // Arrange
        var context1 = InterceptorSubjectContext.Create();
        var context2 = InterceptorSubjectContext.Create();
        context1.AddFallbackContext(context2);

        // Act
        context1.RemoveFallbackContext(context2);
        var fallbacks = context1.GetFallbackContexts();

        // Assert
        Assert.Empty(fallbacks);
    }
}
