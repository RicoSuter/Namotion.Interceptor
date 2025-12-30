using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

public class LifecycleEventsTests
{
    [Fact]
    public void SubjectAttached_FiresWithCorrectReferenceCount()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();

        var lifecycleInterceptor = context.TryGetLifecycleInterceptor();
        Assert.NotNull(lifecycleInterceptor);

        SubjectLifecycleChange? capturedEvent = null;
        lifecycleInterceptor.SubjectAttached += change => capturedEvent = change;

        var parent = new Person(context) { FirstName = "Parent" };
        var child = new Person { FirstName = "Child" };

        // Act
        parent.Father = child;

        // Assert
        Assert.NotNull(capturedEvent);
        Assert.Equal(child, capturedEvent.Value.Subject);
        Assert.Equal(1, capturedEvent.Value.ReferenceCount);
        Assert.NotNull(capturedEvent.Value.Property);
        Assert.Equal("Father", capturedEvent.Value.Property.Value.Name);
    }

    [Fact]
    public void SubjectDetached_FiresWithCorrectReferenceCount()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle()
            .WithContextInheritance();

        var lifecycleInterceptor = context.TryGetLifecycleInterceptor();
        Assert.NotNull(lifecycleInterceptor);

        var detachedEvents = new List<SubjectLifecycleChange>();
        lifecycleInterceptor.SubjectDetached += change => detachedEvents.Add(change);

        var parent = new Person(context) { FirstName = "Parent" };
        var child = new Person { FirstName = "Child" };

        parent.Father = child;

        // Act
        parent.Father = null;

        // Assert - Only one event on last detach (when subject leaves graph)
        var childEvents = detachedEvents.Where(e => e.Subject == child).ToList();
        Assert.Single(childEvents);
        Assert.Equal(0, childEvents[0].ReferenceCount);
    }

    [Fact]
    public void MultipleReferences_EventsFireOnlyOnFirstAttachAndLastDetach()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle()
            .WithContextInheritance();

        var lifecycleInterceptor = context.TryGetLifecycleInterceptor();
        Assert.NotNull(lifecycleInterceptor);

        var attachedEvents = new List<SubjectLifecycleChange>();
        var detachedEvents = new List<SubjectLifecycleChange>();

        lifecycleInterceptor.SubjectAttached += change => attachedEvents.Add(change);
        lifecycleInterceptor.SubjectDetached += change => detachedEvents.Add(change);

        var person = new Person(context) { FirstName = "Person" };
        var parent = new Person { FirstName = "Parent" };

        // Act - Attach same subject via multiple properties
        person.Father = parent;
        person.Mother = parent;

        // Assert - SubjectAttached fires only once on first attach
        var parentAttachEvents = attachedEvents.Where(e => e.Subject == parent).ToList();
        Assert.Single(parentAttachEvents);
        Assert.Equal(1, parentAttachEvents[0].ReferenceCount);

        // Act - Detach one reference (not last, so no event)
        person.Father = null;

        // Assert - No detach event yet (still has Mother reference)
        var parentDetachEvents = detachedEvents.Where(e => e.Subject == parent).ToList();
        Assert.Empty(parentDetachEvents);

        // Act - Detach second reference (last reference)
        person.Mother = null;

        // Assert - SubjectDetached fires only once on last detach
        parentDetachEvents = detachedEvents.Where(e => e.Subject == parent).ToList();
        Assert.Single(parentDetachEvents);
        Assert.Equal(0, parentDetachEvents[0].ReferenceCount);
    }

    [Fact]
    public void MultipleReferencesInArray_EventsFireWithCorrectCounts()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle()
            .WithContextInheritance();

        var lifecycleInterceptor = context.TryGetLifecycleInterceptor();
        Assert.NotNull(lifecycleInterceptor);

        var attachedEvents = new List<SubjectLifecycleChange>();
        var detachedEvents = new List<SubjectLifecycleChange>();

        lifecycleInterceptor.SubjectAttached += change => attachedEvents.Add(change);
        lifecycleInterceptor.SubjectDetached += change => detachedEvents.Add(change);

        var parent = new Person(context) { FirstName = "Parent" };
        var child1 = new Person { FirstName = "Child1" };
        var child2 = new Person { FirstName = "Child2" };

        // Act - Attach children in array
        parent.Children = [child1, child2];

        // Assert - Each child gets one attach event
        var childAttachEvents = attachedEvents.Where(e => e.Subject == child1 || e.Subject == child2).ToList();
        Assert.Equal(2, childAttachEvents.Count); // 2 children * 1 event each
        // child1: property attachment
        Assert.Equal("Children", childAttachEvents[0].Property?.Name);
        Assert.Equal(1, childAttachEvents[0].ReferenceCount);
        Assert.Equal(0, childAttachEvents[0].Index);
        // child2: property attachment
        Assert.Equal("Children", childAttachEvents[1].Property?.Name);
        Assert.Equal(1, childAttachEvents[1].ReferenceCount);
        Assert.Equal(1, childAttachEvents[1].Index);

        // Act - Replace array with one child
        parent.Children = [child2];

        // Assert - child1 fully detached (only one event on last detach)
        var child1DetachEvents = detachedEvents.Where(e => e.Subject == child1).ToList();
        Assert.Single(child1DetachEvents);

        // Act - Clear array
        parent.Children = [];

        // Assert - child2 fully detached (only one event on last detach)
        var child2DetachEvents = detachedEvents.Where(e => e.Subject == child2).ToList();
        Assert.Single(child2DetachEvents);
    }

    [Fact]
    public void TryGetLifecycleInterceptor_ReturnsInterceptor_WhenConfigured()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();

        // Act
        var lifecycleInterceptor = context.TryGetLifecycleInterceptor();

        // Assert
        Assert.NotNull(lifecycleInterceptor);
        Assert.IsType<LifecycleInterceptor>(lifecycleInterceptor);
    }

    [Fact]
    public void TryGetLifecycleInterceptor_ReturnsNull_WhenNotConfigured()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create();

        // Act
        var lifecycleInterceptor = context.TryGetLifecycleInterceptor();

        // Assert
        Assert.Null(lifecycleInterceptor);
    }

    [Fact]
    public void GetReferenceCount_ReturnsZero_ForUnattachedSubject()
    {
        // Arrange
        var child = new Person { FirstName = "Child" };

        // Act
        var count = child.GetReferenceCount();

        // Assert
        Assert.Equal(0, count);
    }

    [Fact]
    public void GetReferenceCount_ReturnsOne_AfterSingleAttach()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();

        var person = new Person(context) { FirstName = "Person" };
        var child = new Person { FirstName = "Child" };

        // Act
        person.Father = child;
        var count = child.GetReferenceCount();

        // Assert
        Assert.Equal(1, count);
    }

    [Fact]
    public void GetReferenceCount_ReturnsZero_AfterDetach()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();

        var person = new Person(context) { FirstName = "Person" };
        var child = new Person { FirstName = "Child" };

        person.Father = child;

        // Act
        person.Father = null;
        var count = child.GetReferenceCount();

        // Assert
        Assert.Equal(0, count);
    }

    [Fact]
    public void GetReferenceCount_ReturnsCorrectCount_WithMultipleReferences()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();

        var person1 = new Person(context) { FirstName = "Person1" };
        var person2 = new Person(context) { FirstName = "Person2" };
        var parent = new Person { FirstName = "Parent" };

        // Act & Assert
        person1.Father = parent;
        Assert.Equal(1, parent.GetReferenceCount());

        person1.Mother = parent;
        Assert.Equal(2, parent.GetReferenceCount());

        person2.Father = parent;
        Assert.Equal(3, parent.GetReferenceCount());

        person1.Father = null;
        Assert.Equal(2, parent.GetReferenceCount());

        person1.Mother = null;
        Assert.Equal(1, parent.GetReferenceCount());

        person2.Father = null;
        Assert.Equal(0, parent.GetReferenceCount());
    }

    [Fact]
    public void GetReferenceCount_ReturnsZero_WhenLifecycleNotEnabled()
    {
        // Arrange - Context without lifecycle
        var context = InterceptorSubjectContext.Create();

        var person = new Person(context) { FirstName = "Person" };
        var child = new Person { FirstName = "Child" };

        // Act
        person.Father = child;
        var count = child.GetReferenceCount();

        // Assert
        Assert.Equal(0, count); // Should return 0 when lifecycle is not enabled
    }

    [Fact]
    public void SubjectAttached_FiresForRootSubject_WhenCreatedWithContext()
    {
        // Arrange
        var attachedEvents = new List<SubjectLifecycleChange>();
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();

        var lifecycleInterceptor = context.TryGetLifecycleInterceptor();
        Assert.NotNull(lifecycleInterceptor);

        lifecycleInterceptor.SubjectAttached += change => attachedEvents.Add(change);

        // Act
        var person = new Person(context) { FirstName = "Person" };

        // Assert
        Assert.Single(attachedEvents);
        Assert.Equal(person, attachedEvents[0].Subject);
        Assert.Equal(0, attachedEvents[0].ReferenceCount); // Root subject has no property reference, so count is 0
        Assert.Null(attachedEvents[0].Property); // Root subject has no parent property
    }

    [Fact]
    public void SubjectDetached_FiresForRootSubject_WhenContextRemoved()
    {
        // Arrange
        var detachedEvents = new List<SubjectLifecycleChange>();
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();

        var lifecycleInterceptor = context.TryGetLifecycleInterceptor();
        Assert.NotNull(lifecycleInterceptor);

        lifecycleInterceptor.SubjectDetached += change => detachedEvents.Add(change);

        var person = new Person(context) { FirstName = "Person" };

        // Act
        ((IInterceptorSubject)person).Context.RemoveFallbackContext(context);

        // Assert
        Assert.Single(detachedEvents);
        Assert.Equal(person, detachedEvents[0].Subject);
        Assert.Equal(0, detachedEvents[0].ReferenceCount);
        Assert.Null(detachedEvents[0].Property); // Root subject has no parent property
    }

    [Fact]
    public void SubjectEvents_DoNotFire_WhenPropertyIsSetToSameValue()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();

        var lifecycleInterceptor = context.TryGetLifecycleInterceptor();
        Assert.NotNull(lifecycleInterceptor);

        var person = new Person(context) { FirstName = "Person" };
        var child = new Person { FirstName = "Child" };

        person.Father = child;

        var attachedEvents = new List<SubjectLifecycleChange>();
        var detachedEvents = new List<SubjectLifecycleChange>();
        lifecycleInterceptor.SubjectAttached += change => attachedEvents.Add(change);
        lifecycleInterceptor.SubjectDetached += change => detachedEvents.Add(change);

        // Act - Set to same value
        person.Father = child;

        // Assert - No new events should fire
        Assert.Empty(attachedEvents);
        Assert.Empty(detachedEvents);
        Assert.Equal(1, child.GetReferenceCount()); // Count should remain 1
    }

    [Fact]
    public void SubjectEvents_FireInCorrectOrder_WhenReplacingReference()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle()
            .WithContextInheritance();

        var lifecycleInterceptor = context.TryGetLifecycleInterceptor();
        Assert.NotNull(lifecycleInterceptor);

        var person = new Person(context) { FirstName = "Person" };
        var child1 = new Person { FirstName = "Child1" };
        var child2 = new Person { FirstName = "Child2" };
        person.Father = child1;

        var events = new List<(string type, IInterceptorSubject subject, int count)>();
        lifecycleInterceptor.SubjectAttached += change =>
            events.Add(("Attached", change.Subject, change.ReferenceCount));
        lifecycleInterceptor.SubjectDetached += change =>
            events.Add(("Detached", change.Subject, change.ReferenceCount));

        // Act - Replace child1 with child2
        person.Father = child2;

        // Assert - Events fire only on first attach / last detach
        // child1: last detach (IsLastDetach=true)
        // child2: first attach (IsFirstAttach=true)
        Assert.Equal(2, events.Count);
        Assert.Equal("Detached", events[0].type);
        Assert.Equal(child1, events[0].subject);
        Assert.Equal(0, events[0].count);
        Assert.Equal("Attached", events[1].type);
        Assert.Equal(child2, events[1].subject);
        Assert.Equal(1, events[1].count);
    }

    [Fact]
    public void SubjectAttached_FiresForContextOnlyAttachment()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();

        var lifecycleInterceptor = context.TryGetLifecycleInterceptor();
        Assert.NotNull(lifecycleInterceptor);

        SubjectLifecycleChange? capturedEvent = null;
        lifecycleInterceptor.SubjectAttached += change => capturedEvent = change;

        // Act - Create subject with context (context-only attachment, no property)
        var person = new Person(context) { FirstName = "Person" };

        // Assert - SubjectAttached always means first attachment
        Assert.NotNull(capturedEvent);
        Assert.Equal(0, capturedEvent.Value.ReferenceCount); // No property reference yet
        Assert.Null(capturedEvent.Value.Property); // Context-only, no property
    }

    [Fact]
    public void SubjectAttached_FiresForFirstPropertyAttachment()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle()
            .WithContextInheritance();

        var lifecycleInterceptor = context.TryGetLifecycleInterceptor();
        Assert.NotNull(lifecycleInterceptor);

        var attachedEvents = new List<SubjectLifecycleChange>();
        lifecycleInterceptor.SubjectAttached += change => attachedEvents.Add(change);

        var parent = new Person(context) { FirstName = "Parent" };
        var child = new Person { FirstName = "Child" };

        // Act - Attach child via property
        parent.Father = child;

        // Assert - Single event (SubjectAttached only fires on first attach)
        var childEvents = attachedEvents.Where(e => e.Subject == child).ToList();
        Assert.Single(childEvents);

        Assert.NotNull(childEvents[0].Property);
        Assert.Equal("Father", childEvents[0].Property!.Value.Name);
        Assert.Equal(1, childEvents[0].ReferenceCount);
    }

    [Fact]
    public void SubjectAttached_DoesNotFireForSecondPropertyAttachment()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();

        var lifecycleInterceptor = context.TryGetLifecycleInterceptor();
        Assert.NotNull(lifecycleInterceptor);

        var person = new Person(context) { FirstName = "Person" };
        var parent = new Person { FirstName = "Parent" };

        person.Father = parent; // First attachment

        var attachedEvents = new List<SubjectLifecycleChange>();
        lifecycleInterceptor.SubjectAttached += change => attachedEvents.Add(change);

        // Act - Attach same parent via second property
        person.Mother = parent;

        // Assert - No event fires (subject already attached, not first attach)
        Assert.Empty(attachedEvents);
    }

    [Fact]
    public void SubjectDetached_DoesNotFireWhenOtherReferencesRemain()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();

        var lifecycleInterceptor = context.TryGetLifecycleInterceptor();
        Assert.NotNull(lifecycleInterceptor);

        var person = new Person(context) { FirstName = "Person" };
        var parent = new Person { FirstName = "Parent" };

        person.Father = parent;
        person.Mother = parent; // Two references

        var detachedEvents = new List<SubjectLifecycleChange>();
        lifecycleInterceptor.SubjectDetached += change => detachedEvents.Add(change);

        // Act - Remove first reference, but second remains
        person.Father = null;

        // Assert - No event fires (subject still has Mother reference, not last detach)
        Assert.Empty(detachedEvents);
    }

    [Fact]
    public void SubjectDetached_FiresWhenLastPropertyReferenceRemoved()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle()
            .WithContextInheritance();

        var lifecycleInterceptor = context.TryGetLifecycleInterceptor();
        Assert.NotNull(lifecycleInterceptor);

        var person = new Person(context) { FirstName = "Person" };
        var child = new Person { FirstName = "Child" };

        person.Father = child; // One reference

        var detachedEvents = new List<SubjectLifecycleChange>();
        lifecycleInterceptor.SubjectDetached += change => detachedEvents.Add(change);

        // Act - Remove the only property reference
        person.Father = null;

        // Assert - Single event fires on last detach (SubjectDetached only fires when subject leaves graph)
        var childEvents = detachedEvents.Where(e => e.Subject == child).ToList();
        Assert.Single(childEvents);
        Assert.Equal(0, childEvents[0].ReferenceCount);
    }

    [Fact]
    public void SubjectDetached_FiresWhenContextRemovedWithNoProperties()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();

        var lifecycleInterceptor = context.TryGetLifecycleInterceptor();
        Assert.NotNull(lifecycleInterceptor);

        var person = new Person(context) { FirstName = "Person" };

        var detachedEvents = new List<SubjectLifecycleChange>();
        lifecycleInterceptor.SubjectDetached += change => detachedEvents.Add(change);

        // Act - Remove context (no property references exist)
        ((IInterceptorSubject)person).Context.RemoveFallbackContext(context);

        // Assert - SubjectDetached fires when subject leaves graph
        Assert.Single(detachedEvents);
        Assert.Equal(0, detachedEvents[0].ReferenceCount); // No property references
        Assert.Null(detachedEvents[0].Property); // Context-only detachment
    }

    [Fact]
    public void ContextDetach_WithPropertyReferencesStillPresent_DoesNotTriggerFinalDetach()
    {
        // This is an edge case: what happens if we manually remove context
        // while property references still exist?
        // The cascade should handle this: removing from property triggers
        // context removal automatically via ContextInheritanceHandler.

        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle()
            .WithContextInheritance();

        var lifecycleInterceptor = context.TryGetLifecycleInterceptor();
        Assert.NotNull(lifecycleInterceptor);

        var parent = new Person(context) { FirstName = "Parent" };
        var child = new Person { FirstName = "Child" };

        parent.Father = child; // Child now has 1 property reference AND inherited context

        var detachedEvents = new List<SubjectLifecycleChange>();
        lifecycleInterceptor.SubjectDetached += change => detachedEvents.Add(change);

        // Act - Try to manually remove the inherited context while property ref exists
        // This is unusual - normally you'd remove from property first
        var childSubject = (IInterceptorSubject)child;
        var parentSubject = (IInterceptorSubject)parent;
        var removed = childSubject.Context.RemoveFallbackContext(parentSubject.Context);

        // Assert - Context should be removed
        Assert.True(removed); // Context was removed

        // The child still has a property reference from parent.Father
        // SubjectDetached should NOT fire because subject still has a property reference
        // Note: Manual context removal while property refs exist is unusual - normally you'd remove from property first
        Assert.DoesNotContain(detachedEvents, e => e.Subject == child);
    }

    [Fact]
    public void FullLifecycle_ContextOnlyThenPropertyThenRemove()
    {
        // Test complete lifecycle: context-only → property attach → property detach → context detach
        // Events only fire on first attach and last detach

        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle()
            .WithContextInheritance();

        var lifecycleInterceptor = context.TryGetLifecycleInterceptor();
        Assert.NotNull(lifecycleInterceptor);

        var attachedEvents = new List<SubjectLifecycleChange>();
        var detachedEvents = new List<SubjectLifecycleChange>();
        lifecycleInterceptor.SubjectAttached += change => attachedEvents.Add(change);
        lifecycleInterceptor.SubjectDetached += change => detachedEvents.Add(change);

        var parent = new Person(context) { FirstName = "Parent" };
        var child = new Person { FirstName = "Child" };

        // Step 1: Context-only attachment (first attach)
        ((IInterceptorSubject)child).Context.AddFallbackContext(context);

        // Assert Step 1 - One attach event fires
        var childAttachEvents = attachedEvents.Where(e => e.Subject == child).ToList();
        Assert.Single(childAttachEvents);

        attachedEvents.Clear();

        // Step 2: Property attachment (not first attach - no event)
        parent.Father = child;

        // Assert Step 2 - No event fires (already attached)
        Assert.DoesNotContain(attachedEvents, e => e.Subject == child);

        // Step 3: Property detachment (last detach - context also removed)
        parent.Father = null;

        // Assert Step 3 - One detach event fires when subject leaves graph
        var childDetachEvents = detachedEvents.Where(e => e.Subject == child).ToList();
        Assert.Single(childDetachEvents);
    }

    /// <summary>
    /// Tests lifecycle events when subject is attached via context first, then property.
    /// </summary>
    [Fact]
    public Task LifecycleEvents_ContextFirstThenProperty()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle()
            .WithContextInheritance();

        var lifecycleInterceptor = context.TryGetLifecycleInterceptor();

        var allEvents = new List<object>();

        lifecycleInterceptor!.SubjectAttached += change =>
        {
            if (change.Subject is Person p && p.FirstName == "Child")
                allEvents.Add(new { Event = "SubjectAttached", change.Property?.Name, change.ReferenceCount });
        };
        lifecycleInterceptor.SubjectDetached += change =>
        {
            if (change.Subject is Person p && p.FirstName == "Child")
                allEvents.Add(new { Event = "SubjectDetached", change.Property?.Name, change.ReferenceCount });
        };

        var parent = new Person(context) { FirstName = "Parent" };
        var child = new Person { FirstName = "Child" }; // No context initially

        // Step 1: Add context via AddFallbackContext (context-only attachment)
        ((IInterceptorSubject)child).Context.AddFallbackContext(context);

        // Step 2: Assign child to property
        parent.Mother = child;

        // Step 3: Remove child from property
        parent.Mother = null;

        return Verify(allEvents);
    }

    /// <summary>
    /// Tests lifecycle events when subject is attached via property with multiple references.
    /// </summary>
    [Fact]
    public Task LifecycleEvents_PropertyAttachmentWithMultipleReferences()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle()
            .WithContextInheritance();

        var lifecycleInterceptor = context.TryGetLifecycleInterceptor();

        var allEvents = new List<object>();

        lifecycleInterceptor!.SubjectAttached += change =>
        {
            if (change.Subject is Person p && p.FirstName == "Child")
                allEvents.Add(new { Event = "SubjectAttached", change.Property?.Name, change.ReferenceCount });
        };
        lifecycleInterceptor.SubjectDetached += change =>
        {
            if (change.Subject is Person p && p.FirstName == "Child")
                allEvents.Add(new { Event = "SubjectDetached", change.Property?.Name, change.ReferenceCount });
        };

        var parent = new Person(context) { FirstName = "Parent" };
        var child = new Person { FirstName = "Child" }; // No context

        // Step 1: Assign child to Mother (first attachment)
        parent.Mother = child;

        // Step 2: Assign same child to Father (second attachment)
        parent.Father = child;

        // Step 3: Remove from Mother (partial detachment)
        parent.Mother = null;

        // Step 4: Remove from Father (final detachment)
        parent.Father = null;

        return Verify(allEvents);
    }

    /// <summary>
    /// Tests all lifecycle events including handler events in correct order.
    /// </summary>
    [Fact]
    public Task LifecycleEvents_AllEventsInOrder()
    {
        // Arrange
        var allEvents = new List<object>();

        var eventCapturingHandler = new EventCapturingLifecycleHandler(allEvents);

        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle()
            .WithContextInheritance()
            .WithService<ILifecycleHandler>(() => eventCapturingHandler, _ => false)
            .WithService<IReferenceLifecycleHandler>(() => eventCapturingHandler, _ => false)
            .WithService<IPropertyLifecycleHandler>(() => eventCapturingHandler, _ => false);

        var lifecycleInterceptor = context.TryGetLifecycleInterceptor();

        lifecycleInterceptor!.SubjectAttached += change =>
        {
            if (change.Subject is Person p && p.FirstName == "Child")
                allEvents.Add(new { Event = "Event:SubjectAttached", change.Property?.Name, change.ReferenceCount });
        };
        lifecycleInterceptor.SubjectDetached += change =>
        {
            if (change.Subject is Person p && p.FirstName == "Child")
                allEvents.Add(new { Event = "Event:SubjectDetached", change.Property?.Name, change.ReferenceCount });
        };

        var parent = new Person(context) { FirstName = "Parent" };
        var child = new Person { FirstName = "Child" };

        // Step 1: Assign child to Mother (first attachment)
        parent.Mother = child;

        // Step 2: Remove from Mother (final detachment)
        parent.Mother = null;

        return Verify(allEvents);
    }

    /// <summary>
    /// Tests that a lifecycle handler can add properties on attach and they are accessible via registry.
    /// This tests the same pattern as MethodPropertyInitializer in HomeBlaze.
    /// </summary>
    [Fact]
    public void LifecycleHandler_CanAddPropertiesOnAttach_AndAccessViaRegistry()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle()
            .WithContextInheritance()
            .WithRegistry()
            .WithService<ILifecycleHandler>(() => new PropertyAddingLifecycleHandler(), _ => false);

        var registry = context.TryGetService<ISubjectRegistry>();
        Assert.NotNull(registry);

        var parent = new Person(context) { FirstName = "Parent" };
        var child = new Person { FirstName = "Child" };

        // Act - Attach child via property
        parent.Father = child;

        // Assert - The handler should have added a "DummyProperty" to the child
        var registeredChild = registry.TryGetRegisteredSubject((IInterceptorSubject)child);
        Assert.NotNull(registeredChild);

        var dummyProperty = registeredChild.TryGetProperty("DummyProperty");
        Assert.NotNull(dummyProperty);
        Assert.Equal(typeof(string), dummyProperty.Type);

        // Verify we can get the value
        var value = dummyProperty.GetValue();
        Assert.Equal("Added by handler", value);
    }

    [Fact]
    public void SubjectAttached_FiresAgainAfterFullDetachAndReattach()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle()
            .WithContextInheritance();

        var lifecycleInterceptor = context.TryGetLifecycleInterceptor();
        Assert.NotNull(lifecycleInterceptor);

        var attachedEvents = new List<SubjectLifecycleChange>();
        var detachedEvents = new List<SubjectLifecycleChange>();
        lifecycleInterceptor.SubjectAttached += change => attachedEvents.Add(change);
        lifecycleInterceptor.SubjectDetached += change => detachedEvents.Add(change);

        var parent = new Person(context) { FirstName = "Parent" };
        var child = new Person { FirstName = "Child" };

        // Act 1: First attachment
        parent.Father = child;

        // Assert 1: SubjectAttached fires on first attachment
        var firstAttachEvents = attachedEvents.Where(e => e.Subject == child).ToList();
        Assert.Single(firstAttachEvents);
        Assert.Equal(1, firstAttachEvents[0].ReferenceCount);

        attachedEvents.Clear();

        // Act 2: Full detachment
        parent.Father = null;

        // Assert 2: Single event fires on last detach
        var detachEvents = detachedEvents.Where(e => e.Subject == child).ToList();
        Assert.Single(detachEvents);

        detachedEvents.Clear();

        // Act 3: Re-attach the same subject
        parent.Mother = child;

        // Assert 3: SubjectAttached fires again after full detachment
        var reattachEvents = attachedEvents.Where(e => e.Subject == child).ToList();
        Assert.Single(reattachEvents);
        Assert.Equal(1, reattachEvents[0].ReferenceCount);
        Assert.Equal("Mother", reattachEvents[0].Property?.Name);
    }
}

/// <summary>
/// Helper handler that captures all lifecycle events for testing event order.
/// </summary>
internal class EventCapturingLifecycleHandler : ILifecycleHandler, IReferenceLifecycleHandler, IPropertyLifecycleHandler
{
    private readonly List<object> _events;

    public EventCapturingLifecycleHandler(List<object> events)
    {
        _events = events;
    }

    public void OnSubjectAttached(SubjectLifecycleChange change)
    {
        if (change.Subject is Person p && p.FirstName == "Child")
            _events.Add(new { Event = "Handler:OnSubjectAttached", change.Property?.Name, change.ReferenceCount });
    }

    public void OnSubjectDetached(SubjectLifecycleChange change)
    {
        if (change.Subject is Person p && p.FirstName == "Child")
            _events.Add(new { Event = "Handler:OnSubjectDetached", change.Property?.Name, change.ReferenceCount });
    }

    public void OnSubjectAttachedToProperty(SubjectLifecycleChange change)
    {
        if (change.Subject is Person p && p.FirstName == "Child")
            _events.Add(new { Event = "Handler:OnSubjectAttachedToProperty", change.Property?.Name, change.ReferenceCount });
    }

    public void OnSubjectDetachedFromProperty(SubjectLifecycleChange change)
    {
        if (change.Subject is Person p && p.FirstName == "Child")
            _events.Add(new { Event = "Handler:OnSubjectDetachedFromProperty", change.Property?.Name, change.ReferenceCount });
    }

    public void OnPropertyAttached(SubjectPropertyLifecycleChange change)
    {
        if (change.Subject is Person p && p.FirstName == "Child")
            _events.Add(new { Event = "Handler:OnPropertyAttached", change.Property.Name });
    }

    public void OnPropertyDetached(SubjectPropertyLifecycleChange change)
    {
        if (change.Subject is Person p && p.FirstName == "Child")
            _events.Add(new { Event = "Handler:OnPropertyDetached", change.Property.Name });
    }
}

/// <summary>
/// Handler that adds a property on attach, similar to MethodPropertyInitializer.
/// Uses change.Context to access the registry (not subject.Context which may not have it yet).
/// </summary>
internal class PropertyAddingLifecycleHandler : ILifecycleHandler
{
    public void OnSubjectAttached(SubjectLifecycleChange change)
    {
        // Use change.Context (the invoking context) to access the registry
        // This is the same pattern as MethodPropertyInitializer
        var registry = change.Context.TryGetService<ISubjectRegistry>();
        var registeredSubject = registry?.TryGetRegisteredSubject(change.Subject);

        if (registeredSubject is null)
        {
            throw new InvalidOperationException("Subject not registered - SubjectRegistry should run before this handler");
        }

        // Add a dummy property - similar to how MethodPropertyInitializer adds method properties
        var dummyValue = "Added by handler";
        registeredSubject.AddProperty(
            "DummyProperty",
            typeof(string),
            _ => dummyValue,
            null,
            []);
    }

    public void OnSubjectDetached(SubjectLifecycleChange change)
    {
        // No cleanup needed
    }
}
