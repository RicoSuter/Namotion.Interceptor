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

        var handler = new TestLifecycleHandler(events);
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

        var handler = new TestLifecycleHandler(events);
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

        var handler = new TestLifecycleHandler(events);
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
        // mother1: first attach (context-only)
        // mother2: first attach + reference added
        mother1.Mother = mother2;
        Assert.Equal(2, events.Count(e => e.StartsWith("Attached: "))); // mother1 + mother2 first attach
        Assert.Single(events, e => e.StartsWith("AttachedToProperty: ")); // mother2 property reference

        // mother3: first attach + reference added
        mother2.Mother = mother3;
        Assert.Equal(3, events.Count(e => e.StartsWith("Attached: "))); // + mother3 first attach
        Assert.Equal(2, events.Count(e => e.StartsWith("AttachedToProperty: "))); // + mother3 property reference

        // Detaching mother2 triggers: reference removed for mother3, mother2, then last detach for mother3, mother2
        mother1.Mother = null;
        Assert.Equal(2, events.Count(e => e.StartsWith("DetachedFromProperty: "))); // mother3, mother2
        Assert.Equal(2, events.Count(e => e.StartsWith("Detached: "))); // mother3, mother2 last detach
    }

    [Fact]
    public Task WhenAddingInterceptorCollection_ThenAllChildrenAreAlsoAttached()
    {
        // Arrange
        var events = new List<string>();

        var handler = new TestLifecycleHandler(events);
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

        var handler = new TestLifecycleHandler(events);
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

        var handler = new TestLifecycleHandler(events);
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

        var handler = new TestLifecycleHandler(events);
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
        // When a ILifecycleHandler adds a property in AttachSubject, the property should be attached only once
        
        // Arrange
        var events = new List<string>();
        var context = CreateContextAndCollectLifecycleEvents(events);
        context.AddService(new AddPropertyToSubjectHandler());

        // Act
        var person = new Person(context);
        
        // Assert
        Assert.Single(events, e => e == "Attached property: { }.FooBar"); // should attach FooBar only once
        return Verify(events);
    }

    public class AddPropertyToSubjectHandler : ILifecycleHandler
    {
        public void OnSubjectAttached(SubjectLifecycleChange change)
        {
            change.Subject.TryGetRegisteredSubject()!.AddProperty("FooBar", typeof(string), _ => "MyValue", null);
        }

        public void OnSubjectDetached(SubjectLifecycleChange change)
        {
            // No cleanup needed
        }
    }

    private static IInterceptorSubjectContext CreateContextAndCollectLifecycleEvents(List<string> events)
    {
        var lifecycleHandlerMock = new Mock<ILifecycleHandler>();
        lifecycleHandlerMock
            .Setup(h => h.OnSubjectAttached(It.IsAny<SubjectLifecycleChange>()))
            .Callback((SubjectLifecycleChange h) => events.Add($"Attached: {h.Subject}"));
        lifecycleHandlerMock
            .Setup(h => h.OnSubjectDetached(It.IsAny<SubjectLifecycleChange>()))
            .Callback((SubjectLifecycleChange h) => events.Add($"Detached: {h.Subject}"));

        var propertyHandlerMock = new Mock<IPropertyLifecycleHandler>();
        propertyHandlerMock
            .Setup(h => h.OnPropertyAttached(It.IsAny<SubjectPropertyLifecycleChange>()))
            .Callback((SubjectPropertyLifecycleChange h) => events.Add($"Attached property: {h.Subject}.{h.Property.Name}"));
        propertyHandlerMock
            .Setup(h => h.OnPropertyDetached(It.IsAny<SubjectPropertyLifecycleChange>()))
            .Callback((SubjectPropertyLifecycleChange h) => events.Add($"Detached property: {h.Subject}.{h.Property.Name}"));

        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        context
            .WithService(() => lifecycleHandlerMock.Object, _ => false)
            .WithService(() => propertyHandlerMock.Object, _ => false);
        return context;
    }

    [Fact]
    public void WhenSubjectAttachedViaContextOnly_ThenReferenceCountShouldBeZero()
    {
        // Arrange - context-only attachment (property=null) should NOT increment reference count
        var events = new List<string>();

        var handler = new TestLifecycleHandler(events);
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle()
            .WithService(() => handler);

        // Act - create subject and attach via AddFallbackContext (no property assignment)
        var person = new Person { FirstName = "John" };
        ((IInterceptorSubject)person).Context.AddFallbackContext(context);

        // Assert - the attach event should show reference count of 0 (context-only)
        var attachEvent = events.Single(e => e.StartsWith("Attached:"));
        Assert.Contains("count: 0", attachEvent);
    }

    [Fact]
    public void WhenSubjectAddedToDictionary_ThenReferenceCountShouldBeOne()
    {
        // Arrange
        var events = new List<string>();

        var handler = new TestLifecycleHandler(events);
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle()
            .WithService(() => handler);

        var container = new Container(context) { Name = "Test" };
        var child = new Person { FirstName = "Child" };

        // Act - add child to dictionary
        container.Children = new Dictionary<string, Person> { { "key1", child } };

        // Assert - child should be attached with reference count 1 (property attachment)
        // Note: there's also a context-only attachment with count: 0 that happens first
        var childAttachEvent = events.Single(e => e.Contains("{Child }") && e.StartsWith("Attached:") && e.Contains("at Children"));
        Assert.Contains("count: 1", childAttachEvent);
    }

    [Fact]
    public void WhenSubjectRemovedFromDictionary_ThenReferenceCountShouldBeZero()
    {
        // Arrange
        var events = new List<string>();

        var handler = new TestLifecycleHandler(events);
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle()
            .WithService(() => handler);

        var container = new Container(context) { Name = "Test" };
        var child = new Person { FirstName = "Child" };

        container.Children = new Dictionary<string, Person> { { "key1", child } };
        events.Clear(); // Clear events to focus on detach

        // Act - remove child by assigning empty dictionary
        container.Children = new Dictionary<string, Person>();

        // Assert - child reference should be removed with reference count 0
        var childReferenceRemovedEvent = events.Single(e => e.Contains("{Child }") && e.StartsWith("DetachedFromProperty:") && e.Contains("at Children"));
        Assert.Contains("count: 0", childReferenceRemovedEvent);
    }

    [Fact]
    public void WhenSubjectWithContextRemovedFromDictionary_ThenReferenceCountShouldBeZero()
    {
        // This is the exact scenario that was failing in HomeBlaze:
        // 1. Subject created and given context via AddFallbackContext
        // 2. Subject added to a Dictionary property
        // 3. Subject removed from Dictionary
        // The reference count should be 0 after step 3, not 1

        // Arrange
        var events = new List<string>();

        var handler = new TestLifecycleHandler(events);
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle()
            .WithService(() => handler);

        var container = new Container(context) { Name = "Test" };

        // Create child and attach via context BEFORE adding to dictionary
        var child = new Person { FirstName = "Child" };
        ((IInterceptorSubject)child).Context.AddFallbackContext(context);

        events.Clear(); // Clear context attachment events

        // Add to dictionary - should increment count to 1
        container.Children = new Dictionary<string, Person> { { "key1", child } };

        // Child was already attached via context, so only reference added happens now
        var referenceAddedEvent = events.Single(e => e.Contains("{Child }") && e.StartsWith("AttachedToProperty:") && e.Contains("at Children"));
        Assert.Contains("count: 1", referenceAddedEvent);

        events.Clear();

        // Act - remove from dictionary - should decrement count to 0
        container.Children = new Dictionary<string, Person>();

        // Assert - reference count should be 0, not 1! (property reference removed)
        var referenceRemovedEvent = events.Single(e => e.Contains("{Child }") && e.StartsWith("DetachedFromProperty:") && e.Contains("at Children"));
        Assert.Contains("count: 0", referenceRemovedEvent);
    }
}
