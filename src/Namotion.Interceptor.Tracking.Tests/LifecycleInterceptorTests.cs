using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests;

public class LifecycleInterceptorTests
{
    [Fact]
    public Task WhenAssigningArray_ThenAllSubjectsAreAttached()
    {
        // Arrange
        var handler = new TestLifecycleHandler();
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
        return Verify(handler.GetEvents());
    }

    [Fact]
    public Task WhenAddingInterceptorCollection_ThenArrayItemsAndParentAreAttached()
    {
        // Arrange
        var handler = new TestLifecycleHandler();
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
        return Verify(handler.GetEvents());
    }

    [Fact]
    public void WhenAssigningSubject_ThenAllSubjectsAreAttached()
    {
        // Arrange
        var handler = new TestLifecycleHandler();
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle()
            .WithService(() => handler)
            .WithContextInheritance();

        // Act
        var mother1 = new Person(context) { FirstName = "Mother1" };
        var mother2 = new Person { FirstName = "Mother2" };
        var mother3 = new Person { FirstName = "Mother3" };

        // Act & Assert
        mother1.Mother = mother2;
        Assert.Equal(2, handler.GetEvents().Count(e => e.Contains("attached")));

        mother2.Mother = mother3;
        Assert.Equal(3, handler.GetEvents().Count(e => e.Contains("attached")));

        mother1.Mother = null;
        Assert.Equal(2, handler.GetEvents().Count(e => e.Contains("detached")));
    }

    [Fact]
    public Task WhenAddingInterceptorCollection_ThenAllChildrenAreAlsoAttached()
    {
        // Arrange
        var handler = new TestLifecycleHandler();
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle()
            .WithService(() => handler)
            .WithContextInheritance();

        // Act
        var mother1 = new Person { FirstName = "Mother1" };
        var mother2 = new Person { FirstName = "Mother2" };
        var mother3 = new Person { FirstName = "Mother3" };

        mother1.Mother = mother2;
        mother2.Mother = mother3;

        ((IInterceptorSubject)mother1).Context.AddFallbackContext(context);

        // Assert
        return Verify(handler.GetEvents());
    }

    [Fact]
    public Task WhenRemovingInterceptors_ThenAllArrayChildrenAreDetached()
    {
        // Arrange
        var handler = new TestLifecycleHandler();
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
        return Verify(handler.GetEvents());
    }

    [Fact]
    public Task WhenRemovingInterceptors_ThenAllChildrenAreDetached()
    {
        // Arrange
        var handler = new TestLifecycleHandler();
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle()
            .WithService(() => handler)
            .WithContextInheritance();

        // Act
        var mother1 = new Person(context) { FirstName = "Mother1" };
        var mother2 = new Person { FirstName = "Mother2" };
        var mother3 = new Person { FirstName = "Mother3" };

        mother1.Mother = mother2;
        mother2.Mother = mother3;
        ((IInterceptorSubject)mother1).Context.RemoveFallbackContext(context);

        // Assert
        return Verify(handler.GetEvents());
    }
    
    [Fact]
    public void WhenChangingProperty_ThenSubjectAttachAndDetachAreCalled()
    {
        // Arrange
        var handler = new TestLifecycleHandler();
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle()
            .WithService(() => handler)
            .WithContextInheritance();

        // Act & Assert
        var car = new Car(context)
        {
            Name = "Test"
        };
        
        Assert.Single(car.Attachements);
        Assert.Empty(car.Detachements);

        var subject = (IInterceptorSubject)car;
        subject.Context.RemoveFallbackContext(context);
        Assert.Single(car.Attachements);
        Assert.Single(car.Detachements);
    }
    
    [Fact]
    public Task WhenSubjectIsAttachedThenAllPropertiesAreAttachedAndSameWithDetach()
    {
        // Arrange
        var handler = new TestLifecycleHandler(trackProperties: true);
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithService(() => handler, _ => false);

        // Act
        var person = new Person
        {
            FirstName = "John",
            LastName = "Doe"
        };

        ((IInterceptorSubject)person).Context.AddFallbackContext(context);

        var father = new Person
        {
            FirstName = "Robert",
            LastName = "Smith"
        };

        person.Father = father;
        father
            .TryGetRegisteredSubject()!
            .AddProperty("FooBar", typeof(string), _ => "MyValue", null);

        person.Father = null;

        // Assert
        return Verify(handler.GetEvents());
    }

    [Fact]
    public Task WhenAddingPropertyInLifecycleHandlerAttach_ThenItIsAttachedOnlyOnce()
    {
        // When a ILifecycleHandler adds a property in AttachSubjectToContext, the property should be attached only once

        // Arrange
        var handler = new TestLifecycleHandler(trackProperties: true);
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithService(() => handler, _ => false);

        context.AddService(new AddPropertyToSubjectHandler());

        // Act
        var person = new Person(context);

        // Assert
        Assert.Single(handler.GetEvents(), e => e == "prop+ NA.FooBar"); // should attach FooBar only once
        return Verify(handler.GetEvents());
    }

    public class AddPropertyToSubjectHandler : ILifecycleHandler
    {
        public void OnLifecycleEvent(SubjectLifecycleChange change)
        {
            if (change.IsContextAttach)
            {
                change.Subject.TryGetRegisteredSubject()!.AddProperty("FooBar", typeof(string), _ => "MyValue", null);
            }
        }
    }
}
