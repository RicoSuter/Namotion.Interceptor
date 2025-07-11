﻿using Castle.Components.DictionaryAdapter.Xml;
using Moq;
using Namotion.Interceptor.Registry;
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
        var attaches = new List<SubjectLifecycleChange>();
        var detaches = new List<SubjectLifecycleChange>();

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
        var attaches = new List<SubjectLifecycleChange>();
        var detaches = new List<SubjectLifecycleChange>();

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
        var attaches = new List<SubjectLifecycleChange>();
        var detaches = new List<SubjectLifecycleChange>();

        var handler = new TestLifecyleHandler(attaches, detaches);
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
        var attaches = new List<SubjectLifecycleChange>();
        var detaches = new List<SubjectLifecycleChange>();

        var handler = new TestLifecyleHandler(attaches, detaches);
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
        Assert.Equal(3, attaches.Count);
    }

    [Fact]
    public void WhenRemovingInterceptors_ThenAllArrayChildrenAreDetached()
    {
        // Arrange
        var attaches = new List<SubjectLifecycleChange>();
        var detaches = new List<SubjectLifecycleChange>();

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
        var attaches = new List<SubjectLifecycleChange>();
        var detaches = new List<SubjectLifecycleChange>();

        var handler = new TestLifecyleHandler(attaches, detaches);
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
        Assert.Equal(3, detaches.Count);
    }
    
    [Fact]
    public void WhenChangingProperty_ThenSubjectAttachAndDetachAreCalled()
    {
        // Arrange
        var attaches = new List<SubjectLifecycleChange>();
        var detaches = new List<SubjectLifecycleChange>();

        var handler = new TestLifecyleHandler(attaches, detaches);
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
        var events = new List<string>();
        var context = CreateContextAndCollectLifecycleEvents(events);

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
            .AddProperty("FooBar", typeof(string), subject => "MyValue", null);

        person.Father = null;

        // Assert
        return Verify(events);
    }

    private static IInterceptorSubjectContext CreateContextAndCollectLifecycleEvents(List<string> events)
    {
        var subjectHandlerMock = new Mock<ILifecycleHandler>();
        subjectHandlerMock
            .Setup(h => h.AttachSubject(It.IsAny<SubjectLifecycleChange>()))
            .Callback((SubjectLifecycleChange h) => events.Add($"Attached: {h.Subject}"));
        subjectHandlerMock
            .Setup(h => h.DetachSubject(It.IsAny<SubjectLifecycleChange>()))
            .Callback((SubjectLifecycleChange h) => events.Add($"Detached: {h.Subject}"));
        
        var propertyHandlerMock = new Mock<IPropertyLifecycleHandler>();
        propertyHandlerMock
            .Setup(h => h.AttachProperty(It.IsAny<SubjectPropertyLifecycleChange>()))
            .Callback((SubjectPropertyLifecycleChange h) => events.Add($"Attached property: {h.Subject}.{h.Property.Name}"));
        propertyHandlerMock
            .Setup(h => h.DetachProperty(It.IsAny<SubjectPropertyLifecycleChange>()))
            .Callback((SubjectPropertyLifecycleChange h) => events.Add($"Detached property: {h.Subject}.{h.Property.Name}"));

        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();
            
        context
            .WithService(() => subjectHandlerMock.Object, _ => false)
            .WithService(() => propertyHandlerMock.Object, _ => false);
        return context;
    }
}
