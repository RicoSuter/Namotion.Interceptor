using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Tests.Updates;

/// <summary>
/// Pins the outbound drop policy for subjects that are momentarily unregistered (detached) when
/// CreatePartialUpdateFromChanges processes queued changes, and the metadata-serialization path
/// that makes the dropped value converge again.
///
/// Scenario:
/// 1. Subject is attached, value changes are queued
/// 2. Subject is detached (concurrent structural mutation)
/// 3. The flush calls CreatePartialUpdateFromChanges with the queued changes
/// 4. TryGetRegisteredProperty() returns null so the change is dropped and counted
/// 5. Convergence comes from the structural change that references the subject: the serializer
///    emits the subject's complete state from its own property metadata
/// </summary>
public class DetachedSubjectUpdateDropTests
{
    [Fact]
    public void WhenValueChangesProcessedWhileSubjectDetached_ThenChangesAreDropped()
    {
        // Arrange: root -> child (attached, registered)
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var root = new Person(context) { FirstName = "Root" };
        var child = new Person { FirstName = "Child" };
        root.Mother = child;

        child.FirstName = "Updated";
        child.LastName = "Name";

        var childId = ((IInterceptorSubject)child).GetOrAddSubjectId();

        var changes = new[]
        {
            SubjectPropertyChange.Create(
                new PropertyReference((IInterceptorSubject)child, "FirstName"),
                ChangeOrigin.Local, DateTimeOffset.UtcNow, null, "Child", "Updated"),
            SubjectPropertyChange.Create(
                new PropertyReference((IInterceptorSubject)child, "LastName"),
                ChangeOrigin.Local, DateTimeOffset.UtcNow, null, (string?)null, "Name"),
        };

        var droppedBefore = SubjectUpdateDiagnostics.DroppedOutboundChanges;

        // Act: detach the child (simulating a concurrent structural mutation) and then build the
        // partial update while the child is unregistered
        root.Mother = null;
        var update = SubjectUpdate.CreatePartialUpdateFromChanges(root, changes, []);

        // Assert: the changes are dropped rather than buffered, and the drop is counted
        Assert.False(
            update.Subjects.ContainsKey(childId),
            "A change for an unregistered subject must be dropped, not serialized.");
        Assert.True(
            SubjectUpdateDiagnostics.DroppedOutboundChanges >= droppedBefore + 2,
            "The drop must be counted. These counters are process-wide, so other tests running in "
            + "parallel can also increment them; assert the floor, not an exact delta.");
    }

    [Fact]
    public void WhenSubjectDetachedAndReattached_ThenValueChangesFromBeforeDetachAreDropped()
    {
        // Arrange: the full scenario - detach, flush, re-attach. The value change that was queued
        // before the detach is dropped; convergence is the job of the structural re-attach update,
        // which serializes the subject's complete state.
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var root = new Person(context) { FirstName = "Root" };
        var child = new Person { FirstName = "Initial" };
        root.Mother = child;

        var childId = ((IInterceptorSubject)child).GetOrAddSubjectId();

        child.FirstName = "Updated";
        var changes = new[]
        {
            SubjectPropertyChange.Create(
                new PropertyReference((IInterceptorSubject)child, "FirstName"),
                ChangeOrigin.Local, DateTimeOffset.UtcNow, null, "Initial", "Updated"),
        };

        var droppedBefore = SubjectUpdateDiagnostics.DroppedOutboundChanges;

        // Act: flush while the child is detached, then re-attach the same instance
        root.Mother = null;
        var updateDuringDetach = SubjectUpdate.CreatePartialUpdateFromChanges(root, changes, []);
        root.Mother = child;

        // Assert: the flush dropped the change, and the re-attach carries the child's complete state
        Assert.Equal("Updated", child.FirstName);
        Assert.False(updateDuringDetach.Subjects.ContainsKey(childId));
        Assert.True(
            SubjectUpdateDiagnostics.DroppedOutboundChanges >= droppedBefore + 1,
            "The drop must be counted. These counters are process-wide, so other tests running in "
            + "parallel can also increment them; assert the floor, not an exact delta.");

        var reattachChange = SubjectPropertyChange.Create(
            new PropertyReference(root, nameof(Person.Mother)),
            ChangeOrigin.Local, DateTimeOffset.UtcNow, null, (Person?)null, child);

        var reattachUpdate = SubjectUpdate.CreatePartialUpdateFromChanges(root, [reattachChange], []);

        Assert.True(reattachUpdate.Subjects.TryGetValue(childId, out var properties));
        Assert.Equal("Updated", properties![nameof(Person.FirstName)].Value);
    }

    [Fact]
    public void WhenChangeArrivesForUnregisteredSubject_ThenChangeIsDroppedAndCounted()
    {
        // Arrange: a value change for a subject that is BOTH momentarily unregistered AND has never
        // been assigned a subject ID. There is no lazy ID minting: the change is dropped and counted
        // rather than serialized under a freshly minted ID.
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var root = new Person(context) { FirstName = "Root" };
        var child = new Person { FirstName = "Child" };
        root.Mother = child;

        // Precondition: the child has no stable ID yet (it was attached but never serialized,
        // and registration alone does not assign an ID).
        Assert.Null(((IInterceptorSubject)child).TryGetSubjectId());

        child.FirstName = "Updated";
        var changes = new[]
        {
            SubjectPropertyChange.Create(
                new PropertyReference((IInterceptorSubject)child, "FirstName"),
                ChangeOrigin.Local, DateTimeOffset.UtcNow, null, "Child", "Updated"),
        };

        var droppedBefore = SubjectUpdateDiagnostics.DroppedOutboundChanges;

        // Act: detach the child (concurrent structural mutation), then build the partial update
        // while it is unregistered and still has no ID
        root.Mother = null;
        var update = SubjectUpdate.CreatePartialUpdateFromChanges(root, changes, []);

        // Assert: no ID was minted and the change was dropped and counted
        Assert.Null(((IInterceptorSubject)child).TryGetSubjectId());
        Assert.DoesNotContain(
            update.Subjects,
            entry => entry.Value.TryGetValue(nameof(Person.FirstName), out var propertyUpdate) &&
                     Equals(propertyUpdate.Value, "Updated"));
        Assert.True(
            SubjectUpdateDiagnostics.DroppedOutboundChanges >= droppedBefore + 1,
            "The drop must be counted. These counters are process-wide, so other tests running in "
            + "parallel can also increment them; assert the floor, not an exact delta.");
    }

    [Fact]
    public void WhenStructuralChangeReferencesUnregisteredSubject_ThenCompleteStateIsSerializedFromMetadata()
    {
        // Arrange - a registered root whose structural change references a subject that has no
        // context and is not registered anywhere. Without the metadata path, the serializer
        // would emit a reference to an ID with no properties entry, which a receiver
        // materializes as a default-valued subject that can never converge.
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var root = new Person(context);
        var unregisteredChild = new Person { FirstName = "Detached" };

        var change = SubjectPropertyChange.Create(
            new PropertyReference(root, nameof(Person.Father)),
            ChangeOrigin.Local,
            DateTimeOffset.UtcNow,
            null,
            oldValue: (Person?)null,
            newValue: unregisteredChild);

        // Act
        var update = SubjectUpdate.CreatePartialUpdateFromChanges(root, [change], []);

        // Assert - the child's ID is referenced AND its complete state is present and marked complete
        var rootId = root.TryGetSubjectId();
        Assert.NotNull(rootId);
        var fatherUpdate = update.Subjects[rootId!][nameof(Person.Father)];
        Assert.Equal(SubjectPropertyUpdateKind.Object, fatherUpdate.Kind);
        Assert.NotNull(fatherUpdate.Id);
        Assert.True(update.Subjects.ContainsKey(fatherUpdate.Id!), "referenced subject must have a properties entry");
        Assert.Equal("Detached", update.Subjects[fatherUpdate.Id!][nameof(Person.FirstName)].Value);
        Assert.NotNull(update.CompleteSubjectIds);
        Assert.Contains(fatherUpdate.Id!, update.CompleteSubjectIds!);
    }
}
