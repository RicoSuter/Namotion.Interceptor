using System.Runtime.CompilerServices;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Tests;

public class DeliveredRevisionFilterTests
{
    [Fact]
    public void WhenACommitWasAlreadyDelivered_ThenAnOlderCommitForThatPropertyIsSuppressed()
    {
        // Arrange: the cross-flush inversion. A writer preempted between committing and enqueuing can
        // land revision 8 in the batch after the one that carried revision 10.
        var tracker = new DeliveredRevisionFilter();
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
        var tracker = new DeliveredRevisionFilter();
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
        var tracker = new DeliveredRevisionFilter();
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
        var tracker = new DeliveredRevisionFilter();
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
    /// Keys hold their subject strongly, so without ageing the filter would keep every subject the
    /// connector ever wrote to alive for as long as it runs. Rotation is what releases them, and it
    /// needs no judgement about whether a property is still live.
    /// </summary>
    [Fact]
    public void WhenPropertiesGoQuiet_ThenRotationReleasesTheirSubjects()
    {
        // Arrange
        var filter = new DeliveredRevisionFilter();
        var quiet = RecordAndAbandon(filter);

        // Act: enough traffic on other subjects to rotate twice, which retires both generations that
        // held the abandoned ones.
        Churn(filter, 9000);

        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);

        // Assert
        Assert.All(quiet, subject => Assert.False(subject.IsAlive,
            "a subject that stopped being written must not be kept alive by the filter"));
    }

    [Fact]
    public void WhenAPropertyKeepsBeingWritten_ThenRotationDoesNotLoseItsBaseline()
    {
        // Arrange: rotation must not drop a property that is still active, or its stragglers would
        // start being admitted again.
        var filter = new DeliveredRevisionFilter();
        var property = new PropertyReference(new Person(), nameof(Person.FirstName));

        // Act: keep it written across enough churn to rotate several times.
        for (var round = 0; round < 3; round++)
        {
            Assert.True(filter.TryAdmit(CreateChange(property, revision: 100 + round)));
            Churn(filter, 5000);
        }

        // Assert
        Assert.False(filter.TryAdmit(CreateChange(property, revision: 50)));
    }

    [Fact]
    public void WhenAnEchoIsRecorded_ThenAnOlderLocalCommitIsSuppressed()
    {
        // Arrange: the source pushed a value in, so it already holds it. A local commit that predates
        // that echo must not be written back over it.
        var filter = new DeliveredRevisionFilter();
        var property = new PropertyReference(new Person(), nameof(Person.FirstName));

        // Act
        filter.RecordDelivered(CreateChange(property, revision: 20));

        // Assert
        Assert.False(filter.TryAdmit(CreateChange(property, revision: 18)));
        Assert.True(filter.TryAdmit(CreateChange(property, revision: 21)));
    }

    [Fact]
    public void WhenTheSourceHoldsTheNewestValue_ThenNoWriteBackIsNeeded()
    {
        // Arrange: nothing this processor wrote is newer, so the source still holds what it was given.
        var filter = new DeliveredRevisionFilter();
        var property = new PropertyReference(new Person(), nameof(Person.FirstName));

        // Act
        filter.RecordDelivered(CreateChange(property, revision: 10));

        // Assert
        Assert.False(filter.WasWrittenOut(property));
    }

    [Fact]
    public void WhenThisProcessorWroteTheProperty_ThenAWriteBackIsNeeded()
    {
        // Arrange: our write may have landed on the source after a transaction's, so a later
        // confirmation has to be sent out to restore the confirmed value.
        var filter = new DeliveredRevisionFilter();
        var property = new PropertyReference(new Person(), nameof(Person.FirstName));

        // Act
        Assert.True(filter.TryAdmit(CreateChange(property, revision: 10)));

        // Assert
        Assert.True(filter.WasWrittenOut(property));
    }

    [Fact]
    public void WhenAConfirmationIsWrittenBack_ThenItDoesNotAskForAnotherWriteBack()
    {
        // Arrange: writing a confirmation out leaves the source holding that same confirmed value, so
        // it is not an overwrite the next confirmation would need to repair.
        var filter = new DeliveredRevisionFilter();
        var subject = new Person();
        var property = new PropertyReference(subject, nameof(Person.FirstName));

        filter.TryAdmit(CreateChange(property, revision: 10));
        Assert.True(filter.WasWrittenOut(property));

        // Act
        var confirmation = SubjectPropertyChange.Create(
            property, ChangeOrigin.Confirmed(new object()), DateTimeOffset.UnixEpoch, null, "old", "new", 11L);
        Assert.True(filter.TryAdmit(confirmation));

        // Assert
        Assert.False(filter.WasWrittenOut(property));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] RecordAndAbandon(DeliveredRevisionFilter filter)
    {
        var abandoned = new WeakReference[500];
        for (var index = 0; index < abandoned.Length; index++)
        {
            var subject = new Person();
            abandoned[index] = new WeakReference(subject);
            filter.TryAdmit(CreateChange(new PropertyReference(subject, nameof(Person.FirstName)), index + 1));
        }

        return abandoned;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Churn(DeliveredRevisionFilter filter, int count)
    {
        for (var index = 0; index < count; index++)
        {
            filter.TryAdmit(CreateChange(new PropertyReference(new Person(), nameof(Person.LastName)), index + 1));
        }
    }

    private static SubjectPropertyChange CreateChange(PropertyReference property, long revision) =>
        SubjectPropertyChange.Create(property, ChangeOrigin.Local, DateTimeOffset.UnixEpoch, null, "old", "new", revision);

    [Fact]
    public void WhenEchoAndFlushTouchTheFilterConcurrently_ThenItStaysConsistent()
    {
        // Arrange: the two real callers. A buffered processor records echoes and answers write-back
        // questions on its dequeue thread while its flush task suppresses an outbound batch, so both
        // reach this map at once. Fresh properties per iteration on purpose: value overwrites of
        // existing entries do not restructure the bucket chains, so only inserts (and the Clear inside
        // rotation) expose the corruption. The count clears RotationThreshold so rotation is covered.
        const int iterations = 20_000;
        var filter = new DeliveredRevisionFilter();
        var echoProperties = new PropertyReference[iterations];
        var flushProperties = new PropertyReference[iterations];
        for (var index = 0; index < iterations; index++)
        {
            echoProperties[index] = new PropertyReference(new Person(), nameof(Person.FirstName));
            flushProperties[index] = new PropertyReference(new Person(), nameof(Person.LastName));
        }

        var barrier = new Barrier(2);
        Exception? dequeueFailure = null;
        Exception? flushFailure = null;

        var dequeueThread = new Thread(() =>
        {
            try
            {
                barrier.SignalAndWait();
                for (var index = 0; index < iterations; index++)
                {
                    filter.WasWrittenOut(echoProperties[index]);
                    filter.RecordDelivered(CreateChange(echoProperties[index], revision: 100));
                }
            }
            catch (Exception exception)
            {
                dequeueFailure = exception;
            }
        });

        var flushThread = new Thread(() =>
        {
            try
            {
                barrier.SignalAndWait();
                var batch = new SubjectPropertyChange[1];
                for (var index = 0; index < iterations; index++)
                {
                    batch[0] = CreateChange(flushProperties[index], revision: 100);
                    filter.SuppressDelivered(batch.AsSpan());
                }
            }
            catch (Exception exception)
            {
                flushFailure = exception;
            }
        });

        // Act
        dequeueThread.Start();
        flushThread.Start();
        dequeueThread.Join();
        flushThread.Join();

        // Assert: the map is structurally intact. Unsynchronized inserts corrupt the bucket chains,
        // which Dictionary detects and reports by throwing out of whichever thread touches it next.
        // Lost updates are covered separately below, because rotation makes them unobservable here.
        Assert.Null(dequeueFailure);
        Assert.Null(flushFailure);
    }

    [Fact]
    public void WhenEchoAndFlushRecordConcurrently_ThenNoBaselineIsLost()
    {
        // Arrange: the same two callers, but deliberately kept under RotationThreshold so nothing is
        // retired and every baseline recorded must still be observable at the end. That makes a lost
        // update assertable, which the rotating test above cannot do. The two threads own disjoint
        // property sets, so a missing baseline is a lost write rather than a legitimate overwrite.
        const int propertiesPerThread = 1_500;
        var filter = new DeliveredRevisionFilter();
        var echoProperties = new PropertyReference[propertiesPerThread];
        var flushProperties = new PropertyReference[propertiesPerThread];
        for (var index = 0; index < propertiesPerThread; index++)
        {
            echoProperties[index] = new PropertyReference(new Person(), nameof(Person.FirstName));
            flushProperties[index] = new PropertyReference(new Person(), nameof(Person.LastName));
        }

        var barrier = new Barrier(2);
        Exception? dequeueFailure = null;
        Exception? flushFailure = null;

        // Captured rather than left to propagate: an unhandled exception on a raw thread takes down the
        // test host, which aborts the run instead of failing this test.
        var dequeueThread = new Thread(() =>
        {
            try
            {
                barrier.SignalAndWait();
                for (var index = 0; index < propertiesPerThread; index++)
                {
                    filter.RecordDelivered(CreateChange(echoProperties[index], revision: 100));
                }
            }
            catch (Exception exception)
            {
                dequeueFailure = exception;
            }
        });

        var flushThread = new Thread(() =>
        {
            try
            {
                barrier.SignalAndWait();
                var batch = new SubjectPropertyChange[1];
                for (var index = 0; index < propertiesPerThread; index++)
                {
                    batch[0] = CreateChange(flushProperties[index], revision: 100);
                    filter.SuppressDelivered(batch.AsSpan());
                }
            }
            catch (Exception exception)
            {
                flushFailure = exception;
            }
        });

        // Act
        dequeueThread.Start();
        flushThread.Start();
        dequeueThread.Join();
        flushThread.Join();

        // Assert: every baseline from both threads still suppresses an older commit. Probing with an
        // older revision does not record, so the check does not disturb what it measures.
        Assert.Null(dequeueFailure);
        Assert.Null(flushFailure);

        for (var index = 0; index < propertiesPerThread; index++)
        {
            Assert.False(filter.TryAdmit(CreateChange(echoProperties[index], revision: 99)),
                $"echo baseline {index} was lost");
            Assert.False(filter.TryAdmit(CreateChange(flushProperties[index], revision: 99)),
                $"flush baseline {index} was lost");
        }
    }
}
