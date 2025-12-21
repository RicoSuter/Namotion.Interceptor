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
            .WithLifecycle();

        var lifecycleInterceptor = context.TryGetLifecycleInterceptor();
        Assert.NotNull(lifecycleInterceptor);

        SubjectLifecycleChange? capturedEvent = null;
        lifecycleInterceptor.SubjectDetached += change => capturedEvent = change;

        var parent = new Person(context) { FirstName = "Parent" };
        var child = new Person { FirstName = "Child" };

        parent.Father = child;

        // Act
        parent.Father = null;

        // Assert
        Assert.NotNull(capturedEvent);
        Assert.Equal(child, capturedEvent.Value.Subject);
        Assert.Equal(0, capturedEvent.Value.ReferenceCount);
        Assert.NotNull(capturedEvent.Value.Property);
        Assert.Equal("Father", capturedEvent.Value.Property.Value.Name);
    }

    [Fact]
    public void MultipleReferences_EventsFireWithIncrementingAndDecrementingCounts()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();

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

        // Assert - SubjectAttached fires twice with incrementing counts
        // Note: attachedEvents[0] is for 'person' itself, [1] and [2] are for 'parent'
        var parentAttachEvents = attachedEvents.Where(e => e.Subject == parent).ToList();
        Assert.Equal(2, parentAttachEvents.Count);
        Assert.Equal(1, parentAttachEvents[0].ReferenceCount);
        Assert.Equal(2, parentAttachEvents[1].ReferenceCount);
        Assert.Equal("Father", parentAttachEvents[0].Property?.Name);
        Assert.Equal("Mother", parentAttachEvents[1].Property?.Name);

        // Act - Detach one reference
        person.Father = null;

        // Assert - SubjectDetached fires with count 1
        Assert.Single(detachedEvents);
        Assert.Equal(parent, detachedEvents[0].Subject);
        Assert.Equal(1, detachedEvents[0].ReferenceCount);
        Assert.Equal("Father", detachedEvents[0].Property?.Name);

        // Act - Detach second reference
        person.Mother = null;

        // Assert - SubjectDetached fires with count 0
        Assert.Equal(2, detachedEvents.Count);
        Assert.Equal(parent, detachedEvents[1].Subject);
        Assert.Equal(0, detachedEvents[1].ReferenceCount);
        Assert.Equal("Mother", detachedEvents[1].Property?.Name);
    }

    [Fact]
    public void MultipleReferencesInArray_EventsFireWithCorrectCounts()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();

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

        // Assert - Both children attached with count 1
        // Note: attachedEvents[0] is for 'parent' itself, [1] and [2] are for children
        var childAttachEvents = attachedEvents.Where(e => e.Subject == child1 || e.Subject == child2).ToList();
        Assert.Equal(2, childAttachEvents.Count);
        Assert.Equal(1, childAttachEvents[0].ReferenceCount);
        Assert.Equal(1, childAttachEvents[1].ReferenceCount);
        Assert.Equal(0, childAttachEvents[0].Index);
        Assert.Equal(1, childAttachEvents[1].Index);

        // Act - Replace array with one child
        parent.Children = [child2];

        // Assert - child1 detached, child2 remains attached (no new attach event for child2)
        Assert.Single(detachedEvents);
        Assert.Equal(child1, detachedEvents[0].Subject);
        Assert.Equal(0, detachedEvents[0].ReferenceCount);
        Assert.Equal(2, childAttachEvents.Count); // No new attach event for child2

        // Act - Clear array
        parent.Children = [];

        // Assert - child2 detached
        Assert.Equal(2, detachedEvents.Count);
        Assert.Equal(child2, detachedEvents[1].Subject);
        Assert.Equal(0, detachedEvents[1].ReferenceCount);
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
            .WithLifecycle();

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

        // Assert - Detach of old value happens before attach of new value
        Assert.Equal(2, events.Count);
        Assert.Equal("Detached", events[0].type);
        Assert.Equal(child1, events[0].subject);
        Assert.Equal(0, events[0].count);
        Assert.Equal("Attached", events[1].type);
        Assert.Equal(child2, events[1].subject);
        Assert.Equal(1, events[1].count);
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
        Assert.False(capturedEvent.Value.IsFinalDetach); // Not detaching
        Assert.Equal(0, capturedEvent.Value.ReferenceCount); // No property reference yet
        Assert.Null(capturedEvent.Value.Property); // Context-only, no property
    }

    [Fact]
    public void IsFirstAttach_TrueForFirstPropertyAttachment()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();

        var lifecycleInterceptor = context.TryGetLifecycleInterceptor();
        Assert.NotNull(lifecycleInterceptor);

        var attachedEvents = new List<SubjectLifecycleChange>();
        lifecycleInterceptor.SubjectAttached += change => attachedEvents.Add(change);

        var parent = new Person(context) { FirstName = "Parent" };
        var child = new Person { FirstName = "Child" };

        // Act - Attach child via property
        parent.Father = child;

        // Assert - Child should have IsFirstAttach=true
        var childEvent = attachedEvents.First(e => e.Subject == child);
        Assert.True(childEvent.IsFirstAttach); // First attachment for child
        Assert.False(childEvent.IsFinalDetach);
        Assert.Equal(1, childEvent.ReferenceCount); // One property reference
        Assert.NotNull(childEvent.Property);
        Assert.Equal("Father", childEvent.Property.Value.Name);
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
        Assert.False(attachedEvents[0].IsFinalDetach);
        Assert.Equal(2, attachedEvents[0].ReferenceCount); // Two property references now
        Assert.Equal("Mother", attachedEvents[0].Property?.Name);
    }

    [Fact]
    public void IsFinalDetach_FalseWhenOtherReferencesRemain()
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

        // Assert - IsFinalDetach should be false (Mother reference still exists)
        Assert.Single(detachedEvents);
        Assert.False(detachedEvents[0].IsFirstAttach);
        Assert.False(detachedEvents[0].IsFinalDetach); // NOT final detach
        Assert.Equal(1, detachedEvents[0].ReferenceCount); // One reference remains
        Assert.Equal("Father", detachedEvents[0].Property?.Name);
    }

    [Fact]
    public void IsFinalDetach_TrueWhenLastPropertyReferenceRemoved()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithLifecycle();

        var lifecycleInterceptor = context.TryGetLifecycleInterceptor();
        Assert.NotNull(lifecycleInterceptor);

        var person = new Person(context) { FirstName = "Person" };
        var child = new Person { FirstName = "Child" };

        person.Father = child; // One reference

        var detachedEvents = new List<SubjectLifecycleChange>();
        lifecycleInterceptor.SubjectDetached += change => detachedEvents.Add(change);

        // Act - Remove the only property reference
        person.Father = null;

        // Assert - IsFinalDetach should be true (no more references)
        Assert.Single(detachedEvents);
        Assert.False(detachedEvents[0].IsFirstAttach);
        Assert.True(detachedEvents[0].IsFinalDetach); // Final detachment
        Assert.Equal(0, detachedEvents[0].ReferenceCount); // No references left
        Assert.Equal("Father", detachedEvents[0].Property?.Name);
    }

    [Fact]
    public void IsFinalDetach_TrueWhenContextRemovedWithNoProperties()
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

        // Assert - IsFinalDetach should be true
        Assert.Single(detachedEvents);
        Assert.False(detachedEvents[0].IsFirstAttach);
        Assert.True(detachedEvents[0].IsFinalDetach); // Final detachment
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

        // Assert - Context should be removed, but IsFinalDetach should be FALSE
        Assert.True(removed); // Context was removed

        // The child still has a property reference from parent.Father
        // So IsFinalDetach should be FALSE even though context was removed
        if (detachedEvents.Any(e => e.Subject == child))
        {
            var childDetachEvent = detachedEvents.First(e => e.Subject == child);
            Assert.False(childDetachEvent.IsFinalDetach); // Not final - property ref remains
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
        Assert.False(contextAttach.IsFinalDetach);
        Assert.Equal(0, contextAttach.ReferenceCount);
        Assert.Null(contextAttach.Property);

        attachedEvents.Clear();

        // Step 2: Property attachment
        parent.Father = child;

        // Assert Step 2
        var propertyAttach = attachedEvents.First(e => e.Subject == child);
        Assert.False(propertyAttach.IsFirstAttach); // Already attached via context
        Assert.False(propertyAttach.IsFinalDetach);
        Assert.Equal(1, propertyAttach.ReferenceCount);
        Assert.Equal("Father", propertyAttach.Property?.Name);

        // Step 3: Property detachment
        parent.Father = null;

        // Assert Step 3 - Two detachment events in cascade
        var propertyDetachEvents = detachedEvents.Where(e => e.Subject == child).ToList();

        // First event: property detachment (Property != null)
        var propertyDetach = propertyDetachEvents.First(e => e.Property != null);
        Assert.False(propertyDetach.IsFirstAttach);
        Assert.False(propertyDetach.IsFinalDetach); // Context still attached
        Assert.Equal(0, propertyDetach.ReferenceCount);
        Assert.Equal("Father", propertyDetach.Property?.Name);

        // Second event: context detachment (Property == null) - via cascade
        var contextDetach = propertyDetachEvents.First(e => e.Property == null);
        Assert.False(contextDetach.IsFirstAttach);
        Assert.True(contextDetach.IsFinalDetach); // Final detachment
        Assert.Equal(0, contextDetach.ReferenceCount);
        Assert.Null(contextDetach.Property);
    }
}
