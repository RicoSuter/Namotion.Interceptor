using Namotion.Interceptor.Tracking.Abstractions;
using Namotion.Interceptor.Tracking.Tests.Mocks;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests;

public class LifecycleInterceptorTests
{
    [Fact]
    public void WhenAssigningArray_ThenAllProxiesAreAttached()
    {
        // Arrange
        var attaches = new List<LifecycleContext>();
        var detaches = new List<LifecycleContext>();

        var handler = new TestProxyPropertyRegistryHandler(attaches, detaches);
        var collection = InterceptorCollection
            .Create()
            .WithProxyLifecycle()
            .WithService<ILifecycleHandler, TestProxyPropertyRegistryHandler>(() => handler);

        // Act
        var mother = new Person(collection) { FirstName = "Mother" };
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
        var collection = InterceptorCollection
            .Create()
            .WithProxyLifecycle()
            .WithService<ILifecycleHandler, TestProxyPropertyRegistryHandler>(() => handler);

        // Act
        var mother = new Person { FirstName = "Mother" };
        var child1 = new Person { FirstName = "Child1" };
        var child2 = new Person { FirstName = "Child2" };

        mother.Children = [child1, child2];
        
        ((IInterceptorSubject)mother).Interceptors.AddInterceptorCollection(collection);

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
        var collection = InterceptorCollection
            .Create()
            .WithInterceptorInheritance()
            .WithProxyLifecycle()
            .WithService<ILifecycleHandler, TestProxyPropertyRegistryHandler>(() => handler);

        // Act
        var mother1 = new Person(collection) { FirstName = "Mother1" };
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
        var collection = InterceptorCollection
            .Create()
            .WithInterceptorInheritance()
            .WithProxyLifecycle()
            .WithService<ILifecycleHandler, TestProxyPropertyRegistryHandler>(() => handler);

        // Act
        var mother1 = new Person { FirstName = "Mother1" };
        var mother2 = new Person { FirstName = "Mother2" };
        var mother3 = new Person { FirstName = "Mother3" };

        mother1.Mother = mother2;
        mother2.Mother = mother3;

        ((IInterceptorSubject)mother1).Interceptors.AddInterceptorCollection(collection);

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
        var collection = InterceptorCollection
            .Create()
            .WithProxyLifecycle()
            .WithService<ILifecycleHandler, TestProxyPropertyRegistryHandler>(() => handler);

        // Act
        var mother = new Person(collection) { FirstName = "Mother" };
        var child1 = new Person { FirstName = "Child1" };
        var child2 = new Person { FirstName = "Child2" };

        mother.Children = [child1, child2];
        ((IInterceptorSubject)mother).Interceptors.RemoveInterceptorCollection(collection);

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
        var collection = InterceptorCollection
            .Create()
            .WithInterceptorInheritance()
            .WithProxyLifecycle()
            .WithService<ILifecycleHandler, TestProxyPropertyRegistryHandler>(() => handler);

        // Act
        var mother1 = new Person(collection) { FirstName = "Mother1" };
        var mother2 = new Person { FirstName = "Mother2" };
        var mother3 = new Person { FirstName = "Mother3" };

        mother1.Mother = mother2;
        mother2.Mother = mother3;

        ((IInterceptorSubject)mother1).Interceptors.RemoveInterceptorCollection(collection);

        // Assert
        Assert.Equal(3, detaches.Count);
    }
}
