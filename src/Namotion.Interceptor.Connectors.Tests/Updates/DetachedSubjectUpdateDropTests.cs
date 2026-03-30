using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Tests.Updates;

/// <summary>
/// Reproduces the convergence failure where value updates for a subject are
/// silently dropped because the subject is momentarily unregistered (detached)
/// at the time CreatePartialUpdateFromChanges processes the queued changes.
///
/// Scenario:
/// 1. Subject is attached, value changes are queued
/// 2. Subject is detached (concurrent structural mutation)
/// 3. CQP flush calls CreatePartialUpdateFromChanges with the queued changes
/// 4. TryGetRegisteredProperty() returns null → changes silently dropped
/// 5. Subject is re-attached but values are never re-broadcast
/// 6. Client has subject with default values permanently
/// </summary>
public class DetachedSubjectUpdateDropTests
{
    [Fact]
    public void WhenValueChangesProcessedWhileSubjectDetached_ThenChangesAreDropped()
    {
        // Arrange: root → child (attached, registered)
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var root = new Person(context) { FirstName = "Root" };
        var child = new Person { FirstName = "Child" };
        root.Mother = child;

        // Make value changes on child (these would be queued by CQP)
        child.FirstName = "Updated";
        child.LastName = "Name";

        // Capture the child's subject ID while still attached
        var childId = ((IInterceptorSubject)child).GetOrAddSubjectId();

        // Capture the value changes as SubjectPropertyChange structs
        // (these would be queued by CQP before the detach happens)
        var changes = new[]
        {
            SubjectPropertyChange.Create(
                new PropertyReference((IInterceptorSubject)child, "FirstName"),
                null, DateTimeOffset.UtcNow, null, "Child", "Updated"),
            SubjectPropertyChange.Create(
                new PropertyReference((IInterceptorSubject)child, "LastName"),
                null, DateTimeOffset.UtcNow, null, (string?)null, "Name"),
        };

        // Act: detach the child (simulating concurrent structural mutation)
        root.Mother = null;

        // Now create partial update with the queued changes — child is detached
        var update = SubjectUpdate.CreatePartialUpdateFromChanges(root, changes, []);

        // Assert: the update should contain the child's value changes,
        // but currently they are silently dropped because TryGetRegisteredProperty() returns null.
        // This is the bug — these changes are lost permanently.
        Assert.True(
            update.Subjects.ContainsKey(childId),
            $"Update should contain subject {childId} but it was dropped because the subject " +
            "was detached at the time of update creation. This causes permanent value loss on clients.");
    }

    [Fact]
    public void WhenSubjectDetachedAndReattached_ThenValueChangesFromBeforeDetachAreLost()
    {
        // This test demonstrates the full scenario: detach, flush, re-attach.
        // After re-attach, the values are correct on the server but the client
        // never receives them because the changes were dropped during flush.
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var root = new Person(context) { FirstName = "Root" };
        var child = new Person { FirstName = "Initial" };
        root.Mother = child;

        // Capture the child's ID while still attached
        var childId = ((IInterceptorSubject)child).GetOrAddSubjectId();

        // Capture value changes
        child.FirstName = "Updated";
        var changes = new[]
        {
            SubjectPropertyChange.Create(
                new PropertyReference((IInterceptorSubject)child, "FirstName"),
                null, DateTimeOffset.UtcNow, null, "Initial", "Updated"),
        };

        // Detach child (concurrent structural mutation removes it briefly)
        root.Mother = null;

        // CQP flush happens while child is detached
        var updateDuringDetach = SubjectUpdate.CreatePartialUpdateFromChanges(root, changes, []);

        // Re-attach the same child instance
        root.Mother = child;

        // The server now has child with FirstName="Updated"
        Assert.Equal("Updated", child.FirstName);

        // The update created during detach should contain the child's value changes
        // using the original ID (which persists in subject.Data after detach).
        var hasChildProperties =
            updateDuringDetach.Subjects.TryGetValue(childId, out var props) &&
            props.ContainsKey("FirstName");

        Assert.True(hasChildProperties,
            "Value changes for a temporarily-detached subject are silently dropped by " +
            "CreatePartialUpdateFromChanges. After re-attachment, no new change notifications " +
            "are generated, so the client permanently has stale/default values.");
    }
}
