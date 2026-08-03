using System.Runtime.CompilerServices;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Tests;

public class EmittedRevisionTrackerTests
{
    [Fact]
    public void WhenACommitWasAlreadyDelivered_ThenAnOlderCommitForThatPropertyIsSuppressed()
    {
        // Arrange: the cross-flush inversion. A writer preempted between committing and enqueuing can
        // land revision 8 in the batch after the one that carried revision 10.
        var tracker = new EmittedRevisionTracker(_ => true);
        var property = new PropertyReference(new Person(), nameof(Person.FirstName));

        // Act
        var newer = tracker.TryAdmit(CreateChange(property, revision: 10));
        var older = tracker.TryAdmit(CreateChange(property, revision: 8));

        // Assert
        Assert.True(newer);
        Assert.False(older);
    }

    [Fact]
    public void WhenANewerCommitArrives_ThenItIsDeliveredAndBecomesTheNewBaseline()
    {
        // Arrange
        var tracker = new EmittedRevisionTracker(_ => true);
        var property = new PropertyReference(new Person(), nameof(Person.FirstName));

        // Act
        tracker.TryAdmit(CreateChange(property, revision: 10));

        // Assert
        Assert.True(tracker.TryAdmit(CreateChange(property, revision: 12)));
        Assert.False(tracker.TryAdmit(CreateChange(property, revision: 12)));
        Assert.False(tracker.TryAdmit(CreateChange(property, revision: 11)));
    }

    [Fact]
    public void WhenAChangeHasNoRevision_ThenItIsNeverSuppressedAndNeverBecomesABaseline()
    {
        // Arrange: revision 0 orders against nothing, so nothing can establish it as superseded, and
        // it must not suppress the real commits that follow it.
        var tracker = new EmittedRevisionTracker(_ => true);
        var property = new PropertyReference(new Person(), nameof(Person.FirstName));

        // Act & Assert
        Assert.True(tracker.TryAdmit(CreateChange(property, revision: 0)));
        Assert.True(tracker.TryAdmit(CreateChange(property, revision: 0)));
        Assert.True(tracker.TryAdmit(CreateChange(property, revision: 5)));
        Assert.True(tracker.TryAdmit(CreateChange(property, revision: 0)));
    }

    [Fact]
    public void WhenPropertiesDiffer_ThenTheirBaselinesAreIndependent()
    {
        // Arrange: revisions are per subject, so two properties of one subject interleave. Suppressing
        // across properties would drop legitimate changes.
        var tracker = new EmittedRevisionTracker(_ => true);
        var subject = new Person();
        var first = new PropertyReference(subject, nameof(Person.FirstName));
        var last = new PropertyReference(subject, nameof(Person.LastName));
        var otherSubject = new PropertyReference(new Person(), nameof(Person.FirstName));

        // Act
        tracker.TryAdmit(CreateChange(first, revision: 10));

        // Assert
        Assert.True(tracker.TryAdmit(CreateChange(last, revision: 8)));
        Assert.True(tracker.TryAdmit(CreateChange(otherSubject, revision: 8)));
    }

    /// <summary>
    /// The tracker keys by <see cref="PropertyReference"/>, which holds its subject strongly, so
    /// without pruning it would keep every subject the connector ever wrote to alive for as long as
    /// the connector runs. Pruning uses the processor's own property filter as the liveness signal:
    /// a detached subject is unregistered and a released property loses its source, so both server and
    /// source filters go false for it.
    /// </summary>
    [Fact]
    public void WhenPropertiesLeaveScope_ThenPruningReleasesTheirSubjects()
    {
        // Arrange: enough properties to cross the prune threshold, all of which then leave scope.
        var live = new HashSet<string>();
        var tracker = new EmittedRevisionTracker(property => live.Contains(property.Name));

        var (subjects, liveSubject) = FillBeyondPruneThreshold(tracker, live);

        // Act: everything except the retained one leaves scope, then one more admitted change trips
        // the prune.
        live.Clear();
        live.Add(nameof(Person.LastName));
        TripPrune(tracker, live);

        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);

        // Assert
        Assert.All(subjects, subject => Assert.False(subject.IsAlive,
            "a subject whose property left the processor's scope must not be kept alive by the tracker"));
        Assert.NotNull(liveSubject);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference[] Collectable, Person Retained) FillBeyondPruneThreshold(
        EmittedRevisionTracker tracker, HashSet<string> live)
    {
        live.Add(nameof(Person.FirstName));
        live.Add(nameof(Person.LastName));

        var collectable = new WeakReference[1200];
        for (var index = 0; index < collectable.Length; index++)
        {
            var subject = new Person();
            collectable[index] = new WeakReference(subject);
            tracker.TryAdmit(CreateChange(new PropertyReference(subject, nameof(Person.FirstName)), index + 1));
        }

        // One subject that stays in scope, to prove pruning is selective rather than a blanket clear.
        var retained = new Person();
        tracker.TryAdmit(CreateChange(new PropertyReference(retained, nameof(Person.LastName)), revision: 1));
        return (collectable, retained);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void TripPrune(EmittedRevisionTracker tracker, HashSet<string> live)
    {
        live.Add(nameof(Person.FirstName_MaxLength_Unit));
        for (var index = 0; index < 1200; index++)
        {
            tracker.TryAdmit(CreateChange(new PropertyReference(new Person(), nameof(Person.FirstName_MaxLength_Unit)), index + 1));
        }
    }

    private static SubjectPropertyChange CreateChange(PropertyReference property, long revision) =>
        SubjectPropertyChange.Create(property, ChangeOrigin.Local, DateTimeOffset.UnixEpoch, null, "old", "new", revision);
}
