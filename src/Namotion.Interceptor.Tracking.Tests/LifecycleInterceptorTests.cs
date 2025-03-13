using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests;

public class LifecycleInterceptorTests
{
    [Fact]
    public void WhenAssigningArray_ThenAllSubjectsAreAttached()
    {
        // Arrange
        var attaches = new List<LifecycleContext>();
        var detaches = new List<LifecycleContext>();

        var handler = new TestLifecyleHandler(attaches, detaches);
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle()
            .WithService(() => handler);

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
    public void WhenAddingInterceptorCollection_ThenArrayItemsAndParentAreAttached()
    {
        // Arrange
        var attaches = new List<LifecycleContext>();
        var detaches = new List<LifecycleContext>();

        var handler = new TestLifecyleHandler(attaches, detaches);
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle()
            .WithService(() => handler);

        // Act
        var mother = new Person { FirstName = "Mother" };
        var child1 = new Person { FirstName = "Child1" };
        var child2 = new Person { FirstName = "Child2" };

        mother.Children = [child1, child2];
        
        ((IInterceptorSubject)mother).Context.AddFallbackContext(context);

        // Assert
        Assert.Equal(3, attaches.Count);
    }

    [Fact]
    public void WhenAssigningSubject_ThenAllSubjectsAreAttached()
    {
        // Arrange
        var attaches = new List<LifecycleContext>();
        var detaches = new List<LifecycleContext>();

        var handler = new TestLifecyleHandler(attaches, detaches);
        var context = InterceptorSubjectContext
            .Create()
            .WithContextInheritance()
            .WithLifecycle()
            .WithService(() => handler);

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
    public void WhenAddingInterceptorCollection_ThenAllChildrenAreAlsoAttached()
    {
        // Arrange
        var attaches = new List<LifecycleContext>();
        var detaches = new List<LifecycleContext>();

        var handler = new TestLifecyleHandler(attaches, detaches);
        var context = InterceptorSubjectContext
            .Create()
            .WithContextInheritance()
            .WithLifecycle()
            .WithService(() => handler);

        // Act
        var mother1 = new Person { FirstName = "Mother1" };
        var mother2 = new Person { FirstName = "Mother2" };
        var mother3 = new Person { FirstName = "Mother3" };

        mother1.Mother = mother2;
        mother2.Mother = mother3;

        ((IInterceptorSubject)mother1).Context.AddFallbackContext(context);

        // Assert
        Assert.Equal(3, attaches.Count);
    }

    [Fact]
    public void WhenRemovingInterceptors_ThenAllArrayChildrenAreDetached()
    {
        // Arrange
        var attaches = new List<LifecycleContext>();
        var detaches = new List<LifecycleContext>();

        var handler = new TestLifecyleHandler(attaches, detaches);
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle()
            .WithService(() => handler);

        // Act
        var mother = new Person(context) { FirstName = "Mother" };
        var child1 = new Person { FirstName = "Child1" };
        var child2 = new Person { FirstName = "Child2" };

        mother.Children = [child1, child2];
        ((IInterceptorSubject)mother).Context.RemoveFallbackContext(context);

        // Assert
        Assert.Equal(3, detaches.Count);
    }

    [Fact]
    public void WhenRemovingInterceptors_ThenAllChildrenAreDetached()
    {
        // Arrange
        var attaches = new List<LifecycleContext>();
        var detaches = new List<LifecycleContext>();

        var handler = new TestLifecyleHandler(attaches, detaches);
        var context = InterceptorSubjectContext
            .Create()
            .WithContextInheritance()
            .WithLifecycle()
            .WithService(() => handler);

        // Act
        var mother1 = new Person(context) { FirstName = "Mother1" };
        var mother2 = new Person { FirstName = "Mother2" };
        var mother3 = new Person { FirstName = "Mother3" };

        mother1.Mother = mother2;
        mother2.Mother = mother3;
        ((IInterceptorSubject)mother1).Context.RemoveFallbackContext(context);

        // Assert
        Assert.Equal(3, detaches.Count);
    }
}
