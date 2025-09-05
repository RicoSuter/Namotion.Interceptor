using Moq;
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
        var events = new List<string>();

        var handler = new TestLifecyleHandler(events);
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
        return Verify(events);
    }

    [Fact]
    public Task WhenAddingInterceptorCollection_ThenArrayItemsAndParentAreAttached()
    {
        // Arrange
        var events = new List<string>();

        var handler = new TestLifecyleHandler(events);
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
        return Verify(events);
    }

    [Fact]
    public void WhenAssigningSubject_ThenAllSubjectsAreAttached()
    {
        // Arrange
        var events = new List<string>();

        var handler = new TestLifecyleHandler(events);
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
        Assert.Equal(2, events.Count(e => e.StartsWith("Attached: ")));

        mother2.Mother = mother3;
        Assert.Equal(3, events.Count(e => e.StartsWith("Attached: ")));

        mother1.Mother = null;
        Assert.Equal(2, events.Count(e => e.StartsWith("Detached: ")));
    }

    [Fact]
    public Task WhenAddingInterceptorCollection_ThenAllChildrenAreAlsoAttached()
    {
        // Arrange
        var events = new List<string>();

        var handler = new TestLifecyleHandler(events);
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
        return Verify(events);
    }

    [Fact]
    public Task WhenRemovingInterceptors_ThenAllArrayChildrenAreDetached()
    {
        // Arrange
        var events = new List<string>();

        var handler = new TestLifecyleHandler(events);
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
        return Verify(events);
    }

    [Fact]
    public Task WhenRemovingInterceptors_ThenAllChildrenAreDetached()
    {
        // Arrange
        var events = new List<string>();

        var handler = new TestLifecyleHandler(events);
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
        return Verify(events);
    }
    
    [Fact]
    public void WhenChangingProperty_ThenSubjectAttachAndDetachAreCalled()
    {
        // Arrange
        var events = new List<string>();

        var handler = new TestLifecyleHandler(events);
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
            .AddProperty("FooBar", typeof(string), _ => "MyValue", null);

        person.Father = null;

        // Assert
        return Verify(events);
    }
    
    [Fact]
    public Task WhenAddingPropertyInLifecycleHandlerAttach_ThenItIsAttachedOnlyOnce()
    {
        // Arrange
        var events = new List<string>();
        var context = CreateContextAndCollectLifecycleEvents(events);
        context.AddService(new AddPropertyToSubjectHandler());

        // Act
        var person = new Person(context);
        
        // Assert
        Assert.Single(events, e => e == "Attached property:  .FooBar"); // should attach FooBar only once
        return Verify(events);
    }

    public class AddPropertyToSubjectHandler : ILifecycleHandler
    {
        public void AttachSubject(SubjectLifecycleChange change)
        {
            change.Subject.TryGetRegisteredSubject()!.AddProperty("FooBar", typeof(string), _ => "MyValue", null);
        }

        public void DetachSubject(SubjectLifecycleChange change)
        {
        }
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
