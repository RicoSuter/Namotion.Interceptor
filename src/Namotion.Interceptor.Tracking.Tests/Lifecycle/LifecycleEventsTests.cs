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

        // Assert - Two events: property detach first, then context-only detach
        var childEvents = detachedEvents.Where(e => e.Subject == child).ToList();
        Assert.Equal(2, childEvents.Count);
        // First event: property detach
        Assert.False(childEvents[0].IsLastDetach);
        Assert.NotNull(childEvents[0].Property);
        Assert.Equal("Father", childEvents[0].Property!.Value.Name);
        Assert.Equal(0, childEvents[0].ReferenceCount);
        // Second event: context-only detach (IsLastDetach=true)
        Assert.True(childEvents[1].IsLastDetach);
        Assert.Null(childEvents[1].Property);
        Assert.Equal(0, childEvents[1].ReferenceCount);
    }

    [Fact]
    public void MultipleReferences_EventsFireWithIncrementingAndDecrementingCounts()
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

        // Assert - SubjectAttached fires twice: once per property
        var parentAttachEvents = attachedEvents.Where(e => e.Subject == parent).ToList();
        Assert.Equal(2, parentAttachEvents.Count);
        // First: Father property attachment (IsFirstAttach=true)
        Assert.Equal("Father", parentAttachEvents[0].Property?.Name);
        Assert.True(parentAttachEvents[0].IsFirstAttach);
        Assert.Equal(1, parentAttachEvents[0].ReferenceCount);
        // Second: Mother property attachment (IsFirstAttach=false)
        Assert.Equal("Mother", parentAttachEvents[1].Property?.Name);
        Assert.False(parentAttachEvents[1].IsFirstAttach);
        Assert.Equal(2, parentAttachEvents[1].ReferenceCount);

        // Act - Detach one reference
        person.Father = null;

        // Assert - SubjectDetached fires with count 1
        var parentDetachEvents = detachedEvents.Where(e => e.Subject == parent).ToList();
        Assert.Single(parentDetachEvents);
        Assert.Equal(1, parentDetachEvents[0].ReferenceCount);
        Assert.Equal("Father", parentDetachEvents[0].Property?.Name);

        // Act - Detach second reference
        person.Mother = null;

        // Assert - SubjectDetached fires: Mother property first, then context-only
        parentDetachEvents = detachedEvents.Where(e => e.Subject == parent).ToList();
        Assert.Equal(3, parentDetachEvents.Count);
        // Second detach: Mother property
        Assert.Equal(0, parentDetachEvents[1].ReferenceCount);
        Assert.Equal("Mother", parentDetachEvents[1].Property?.Name);
        Assert.False(parentDetachEvents[1].IsLastDetach);
        // Third detach: context-only (IsLastDetach=true)
        Assert.Null(parentDetachEvents[2].Property);
        Assert.True(parentDetachEvents[2].IsLastDetach);
        Assert.Equal(0, parentDetachEvents[2].ReferenceCount);
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

        // Assert - Each child gets one attach event with IsFirstAttach=true
        var childAttachEvents = attachedEvents.Where(e => e.Subject == child1 || e.Subject == child2).ToList();
        Assert.Equal(2, childAttachEvents.Count); // 2 children * 1 event each
        // child1: property attachment with IsFirstAttach=true
        Assert.Equal("Children", childAttachEvents[0].Property?.Name);
        Assert.True(childAttachEvents[0].IsFirstAttach);
        Assert.Equal(1, childAttachEvents[0].ReferenceCount);
        Assert.Equal(0, childAttachEvents[0].Index);
        // child2: property attachment with IsFirstAttach=true
        Assert.Equal("Children", childAttachEvents[1].Property?.Name);
        Assert.True(childAttachEvents[1].IsFirstAttach);
        Assert.Equal(1, childAttachEvents[1].ReferenceCount);
        Assert.Equal(1, childAttachEvents[1].Index);

        // Act - Replace array with one child
        parent.Children = [child2];

        // Assert - child1 fully detached: property first, then context-only
        var child1DetachEvents = detachedEvents.Where(e => e.Subject == child1).ToList();
        Assert.Equal(2, child1DetachEvents.Count);
        Assert.Equal("Children", child1DetachEvents[0].Property?.Name);
        Assert.False(child1DetachEvents[0].IsLastDetach);
        Assert.Null(child1DetachEvents[1].Property);
        Assert.True(child1DetachEvents[1].IsLastDetach);

        // Act - Clear array
        parent.Children = [];

        // Assert - child2 fully detached: property first, then context-only
        var child2DetachEvents = detachedEvents.Where(e => e.Subject == child2).ToList();
        Assert.Equal(2, child2DetachEvents.Count);
        Assert.Equal("Children", child2DetachEvents[0].Property?.Name);
        Assert.False(child2DetachEvents[0].IsLastDetach);
        Assert.Null(child2DetachEvents[1].Property);
        Assert.True(child2DetachEvents[1].IsLastDetach);
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

        // Assert - Detach old (property first, then context-only) then attach new
        // child1: property detach + context-only detach (IsLastDetach=true)
        // child2: property attach (count 1, IsFirstAttach)
        Assert.Equal(3, events.Count);
        Assert.Equal("Detached", events[0].type);
        Assert.Equal(child1, events[0].subject); // Property detach
        Assert.Equal(0, events[0].count);
        Assert.Equal("Detached", events[1].type);
        Assert.Equal(child1, events[1].subject); // Context-only detach
        Assert.Equal(0, events[1].count);
        Assert.Equal("Attached", events[2].type);
        Assert.Equal(child2, events[2].subject); // Property attach with IsFirstAttach
        Assert.Equal(1, events[2].count);
    }

    [Fact]
    public void IsFirstAttach_TrueForContextOnlyAttachment()
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

        // Assert
        Assert.NotNull(capturedEvent);
        Assert.True(capturedEvent.Value.IsFirstAttach); // First attachment
        Assert.False(capturedEvent.Value.IsLastDetach); // Not detaching
        Assert.Equal(0, capturedEvent.Value.ReferenceCount); // No property reference yet
        Assert.Null(capturedEvent.Value.Property); // Context-only, no property
    }

    [Fact]
    public void IsFirstAttach_TrueForFirstPropertyAttachment()
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

        // Assert - Single event with IsFirstAttach=true
        var childEvents = attachedEvents.Where(e => e.Subject == child).ToList();
        Assert.Single(childEvents);

        Assert.True(childEvents[0].IsFirstAttach);
        Assert.NotNull(childEvents[0].Property);
        Assert.Equal("Father", childEvents[0].Property!.Value.Name);
        Assert.Equal(1, childEvents[0].ReferenceCount);
    }

    [Fact]
    public void IsFirstAttach_FalseForSecondPropertyAttachment()
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

        // Assert - Second attachment should have IsFirstAttach=false
        Assert.Single(attachedEvents);
        Assert.False(attachedEvents[0].IsFirstAttach); // NOT first attachment
        Assert.False(attachedEvents[0].IsLastDetach);
        Assert.Equal(2, attachedEvents[0].ReferenceCount); // Two property references now
        Assert.Equal("Mother", attachedEvents[0].Property?.Name);
    }

    [Fact]
    public void IsLastDetach_FalseWhenOtherReferencesRemain()
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

        // Assert - IsLastDetach should be false (Mother reference still exists)
        Assert.Single(detachedEvents);
        Assert.False(detachedEvents[0].IsFirstAttach);
        Assert.False(detachedEvents[0].IsLastDetach); // NOT final detach
        Assert.Equal(1, detachedEvents[0].ReferenceCount); // One reference remains
        Assert.Equal("Father", detachedEvents[0].Property?.Name);
    }

    [Fact]
    public void IsLastDetach_TrueWhenLastPropertyReferenceRemoved()
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

        // Assert - Two detach events: property detach first, then context-only
        var childEvents = detachedEvents.Where(e => e.Subject == child).ToList();
        Assert.Equal(2, childEvents.Count);
        // First: property detach
        Assert.False(childEvents[0].IsLastDetach);
        Assert.Equal(0, childEvents[0].ReferenceCount);
        Assert.Equal("Father", childEvents[0].Property?.Name);
        // Second: context-only detach (final detachment)
        Assert.True(childEvents[1].IsLastDetach);
        Assert.Null(childEvents[1].Property);
        Assert.Equal(0, childEvents[1].ReferenceCount);
    }

    [Fact]
    public void IsLastDetach_TrueWhenContextRemovedWithNoProperties()
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

        // Assert - IsLastDetach should be true
        Assert.Single(detachedEvents);
        Assert.False(detachedEvents[0].IsFirstAttach);
        Assert.True(detachedEvents[0].IsLastDetach); // Final detachment
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

        // Assert - Context should be removed, but IsLastDetach should be FALSE
        Assert.True(removed); // Context was removed

        // The child still has a property reference from parent.Father
        // So IsLastDetach should be FALSE even though context was removed
        if (detachedEvents.Any(e => e.Subject == child))
        {
            var childDetachEvent = detachedEvents.First(e => e.Subject == child);
            Assert.False(childDetachEvent.IsLastDetach); // Not final - property ref remains
            Assert.Equal(1, childDetachEvent.ReferenceCount); // Property reference still exists
        }
    }

    [Fact]
    public void FullLifecycle_ContextOnlyThenPropertyThenRemove()
    {
        // Test complete lifecycle: context-only → property attach → property detach → context detach

        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle()
            .WithContextInheritance(); // Need context inheritance for cascade

        var lifecycleInterceptor = context.TryGetLifecycleInterceptor();
        Assert.NotNull(lifecycleInterceptor);

        var attachedEvents = new List<SubjectLifecycleChange>();
        var detachedEvents = new List<SubjectLifecycleChange>();
        lifecycleInterceptor.SubjectAttached += change => attachedEvents.Add(change);
        lifecycleInterceptor.SubjectDetached += change => detachedEvents.Add(change);

        var parent = new Person(context) { FirstName = "Parent" };
        var child = new Person { FirstName = "Child" };

        // Step 1: Context-only attachment (child created without context)
        ((IInterceptorSubject)child).Context.AddFallbackContext(context);

        // Assert Step 1
        var contextAttach = attachedEvents.First(e => e.Subject == child);
        Assert.True(contextAttach.IsFirstAttach);
        Assert.False(contextAttach.IsLastDetach);
        Assert.Equal(0, contextAttach.ReferenceCount);
        Assert.Null(contextAttach.Property);

        attachedEvents.Clear();

        // Step 2: Property attachment
        parent.Father = child;

        // Assert Step 2
        var propertyAttach = attachedEvents.First(e => e.Subject == child);
        Assert.False(propertyAttach.IsFirstAttach); // Already attached via context
        Assert.False(propertyAttach.IsLastDetach);
        Assert.Equal(1, propertyAttach.ReferenceCount);
        Assert.Equal("Father", propertyAttach.Property?.Name);

        // Step 3: Property detachment triggers two events:
        // Property detach fires first (event fires before handlers)
        // Context detach fires second (nested call from ContextInheritanceHandler)
        parent.Father = null;

        Assert.Equal(2, detachedEvents.Count(e => e.Subject == child));

        // First event: Property detachment (fires before handlers)
        var propertyDetach = detachedEvents.First(e => e.Subject == child);
        Assert.False(propertyDetach.IsFirstAttach);
        Assert.False(propertyDetach.IsLastDetach);
        Assert.Equal(0, propertyDetach.ReferenceCount);
        Assert.Equal("Father", propertyDetach.Property?.Name);

        // Second event: Context detachment (nested call from ContextInheritanceHandler)
        var contextDetach = detachedEvents.Last(e => e.Subject == child);
        Assert.False(contextDetach.IsFirstAttach);
        Assert.True(contextDetach.IsLastDetach); // Final - set now empty
        Assert.Equal(0, contextDetach.ReferenceCount);
        Assert.Null(contextDetach.Property);
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
                allEvents.Add(new { Event = "SubjectAttached", change.Property?.Name, change.ReferenceCount, change.IsFirstAttach, change.IsLastDetach });
        };
        lifecycleInterceptor.SubjectDetached += change =>
        {
            if (change.Subject is Person p && p.FirstName == "Child")
                allEvents.Add(new { Event = "SubjectDetached", change.Property?.Name, change.ReferenceCount, change.IsFirstAttach, change.IsLastDetach });
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
                allEvents.Add(new { Event = "SubjectAttached", change.Property?.Name, change.ReferenceCount, change.IsFirstAttach, change.IsLastDetach });
        };
        lifecycleInterceptor.SubjectDetached += change =>
        {
            if (change.Subject is Person p && p.FirstName == "Child")
                allEvents.Add(new { Event = "SubjectDetached", change.Property?.Name, change.ReferenceCount, change.IsFirstAttach, change.IsLastDetach });
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

    [Fact]
    public void IsFirstAttach_TrueAgainAfterFullDetachAndReattach()
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

        // Assert 1: IsFirstAttach=true on first attachment
        var firstAttachEvents = attachedEvents.Where(e => e.Subject == child).ToList();
        Assert.Single(firstAttachEvents);
        Assert.True(firstAttachEvents[0].IsFirstAttach);
        Assert.Equal(1, firstAttachEvents[0].ReferenceCount);

        attachedEvents.Clear();

        // Act 2: Full detachment
        parent.Father = null;

        // Assert 2: IsLastDetach=true on final detachment
        var detachEvents = detachedEvents.Where(e => e.Subject == child).ToList();
        Assert.Equal(2, detachEvents.Count);
        Assert.False(detachEvents[0].IsLastDetach); // Property detach
        Assert.True(detachEvents[1].IsLastDetach);  // Context-only detach (final)

        detachedEvents.Clear();

        // Act 3: Re-attach the same subject
        parent.Mother = child;

        // Assert 3: IsFirstAttach=true again after full detachment
        var reattachEvents = attachedEvents.Where(e => e.Subject == child).ToList();
        Assert.Single(reattachEvents);
        Assert.True(reattachEvents[0].IsFirstAttach); // Should be true again!
        Assert.Equal(1, reattachEvents[0].ReferenceCount);
        Assert.Equal("Mother", reattachEvents[0].Property?.Name);
    }
}
