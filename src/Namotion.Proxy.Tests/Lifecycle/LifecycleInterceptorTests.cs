using Namotion.Interception.Lifecycle.Abstractions;
using Namotion.Interceptor;

namespace Namotion.Proxy.Tests.Lifecycle;

public class LifecycleInterceptorTests
{
    [Fact]
    public void WhenAssigningArray_ThenAllProxiesAreAttached()
    {
        // Arrange
        var attaches = new List<LifecycleContext>();
        var detaches = new List<LifecycleContext>();

        var handler = new TestProxyPropertyRegistryHandler(attaches, detaches);
        var context = InterceptorProvider
            .CreateBuilder()
            .WithProxyLifecycle()
            .TryAddSingleton<ILifecycleHandler, TestProxyPropertyRegistryHandler>(_ => handler)
            .Build();

        // Act
        var mother = new Person(context) { FirstName = "Mother" };
        var child1 = new Person { FirstName = "Child1" };
        var child2 = new Person { FirstName = "Child2" };

        mother.Children = [child1, child2];
        mother.Children = [child1]; // should only detach child2

        // Assert
        Assert.Equal(3, attaches.Count);
        Assert.Single(detaches);
    }

    [Fact]
    public void WhenCallingSetContext_ThenArrayItemsAreAttached()
    {
        // Arrange
        var attaches = new List<LifecycleContext>();
        var detaches = new List<LifecycleContext>();

        var handler = new TestProxyPropertyRegistryHandler(attaches, detaches);
        var context = InterceptorProvider
            .CreateBuilder()
            .WithProxyLifecycle()
            .TryAddSingleton<ILifecycleHandler, TestProxyPropertyRegistryHandler>(_ => handler)
            .Build();

        // Act
        var mother = new Person { FirstName = "Mother" };
        var child1 = new Person { FirstName = "Child1" };
        var child2 = new Person { FirstName = "Child2" };

        mother.Children = [child1, child2];
        
        mother.AddInterceptors(context);

        // Assert
        Assert.Equal(3, attaches.Count);
    }

    [Fact]
    public void WhenAssigningProxy_ThenAllProxyIsAttached()
    {
        // Arrange
        var attaches = new List<LifecycleContext>();
        var detaches = new List<LifecycleContext>();

        var handler = new TestProxyPropertyRegistryHandler(attaches, detaches);
        var context = InterceptorProvider
            .CreateBuilder()
            .WithInterceptorInheritance()
            .WithProxyLifecycle()
            .TryAddSingleton<ILifecycleHandler, TestProxyPropertyRegistryHandler>(_ => handler)
            .Build();

        // Act
        var mother1 = new Person(context) { FirstName = "Mother1" };
        var mother2 = new Person { FirstName = "Mother2" };
        var mother3 = new Person { FirstName = "Mother3" };

        // Act & Assert
        mother1.Mother = mother2;
        Assert.Equal(2, attaches.Count);

        mother2.Mother = mother3;
        Assert.Equal(3, attaches.Count);

        mother1.Mother = null;
        Assert.Equal(2, detaches.Count);
    }

    [Fact]
    public void WhenCallingSetContext_ThenAllChildrenAreAlsoAttached()
    {
        // Arrange
        var attaches = new List<LifecycleContext>();
        var detaches = new List<LifecycleContext>();

        var handler = new TestProxyPropertyRegistryHandler(attaches, detaches);
        var context = InterceptorProvider
            .CreateBuilder()
            .WithInterceptorInheritance()
            .WithProxyLifecycle()
            .TryAddSingleton<ILifecycleHandler, TestProxyPropertyRegistryHandler>(_ => handler)
            .Build();

        // Act
        var mother1 = new Person { FirstName = "Mother1" };
        var mother2 = new Person { FirstName = "Mother2" };
        var mother3 = new Person { FirstName = "Mother3" };

        mother1.Mother = mother2;
        mother2.Mother = mother3;

        mother1.AddInterceptors(context);

        // Assert
        Assert.Equal(3, attaches.Count);
    }

    [Fact]
    public void WhenRemovingInterceptors_ThenAllItemsAreDetached()
    {
        // Arrange
        var attaches = new List<LifecycleContext>();
        var detaches = new List<LifecycleContext>();

        var handler = new TestProxyPropertyRegistryHandler(attaches, detaches);
        var context = InterceptorProvider
            .CreateBuilder()
            .WithProxyLifecycle()
            .TryAddSingleton<ILifecycleHandler, TestProxyPropertyRegistryHandler>(_ => handler)
            .Build();

        // Act
        var mother = new Person(context) { FirstName = "Mother" };
        var child1 = new Person { FirstName = "Child1" };
        var child2 = new Person { FirstName = "Child2" };

        mother.Children = [child1, child2];
        mother.RemoveInterceptors(context);

        // Assert
        Assert.Equal(3, detaches.Count);
    }

    [Fact]
    public void WhenRemovingInterceptors_ThenAllChildrenAreDetached()
    {
        // Arrange
        var attaches = new List<LifecycleContext>();
        var detaches = new List<LifecycleContext>();

        var handler = new TestProxyPropertyRegistryHandler(attaches, detaches);
        var context = InterceptorProvider
            .CreateBuilder()
            .WithInterceptorInheritance()
            .WithProxyLifecycle()
            .TryAddSingleton<ILifecycleHandler, TestProxyPropertyRegistryHandler>(_ => handler)
            .Build();

        // Act
        var mother1 = new Person(context) { FirstName = "Mother1" };
        var mother2 = new Person { FirstName = "Mother2" };
        var mother3 = new Person { FirstName = "Mother3" };

        mother1.Mother = mother2;
        mother2.Mother = mother3;

        mother1.RemoveInterceptors(context);

        // Assert
        Assert.Equal(3, detaches.Count);
    }
}
